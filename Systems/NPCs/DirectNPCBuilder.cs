using System;
using UnityEngine;
using Behind_Bars.Helpers;
using HarmonyLib;

#if !MONO
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Noise;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.UI.WorldspacePopup;
using Il2CppScheduleOne.Vision;
using Il2CppScheduleOne.NPCs.Responses;
using Il2CppScheduleOne.UI.Phone.ContactsApp;
using Il2CppScheduleOne.DevUtilities;
#else
using ScheduleOne.NPCs;
using ScheduleOne.Noise;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.Interaction;
using ScheduleOne.Vehicles;
using ScheduleOne.AvatarFramework;
using ScheduleOne.Messaging;
using ScheduleOne.UI.WorldspacePopup;
using ScheduleOne.Vision;
using ScheduleOne.NPCs.Responses;
using ScheduleOne.UI.Phone.ContactsApp;
using ScheduleOne.DevUtilities;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Creates NPCs from scratch using Schedule One's native components in proper order.
    /// Based on S1API analysis but without dependencies. Fixes physics issues with cloning approach.
    /// </summary>
    public static class DirectNPCBuilder
    {
        public enum NPCType
        {
            JailGuard,
            JailInmate,
            GenericJailStaff,
            ParoleOfficer,
            TestNPC
        }

        /// <summary>
        /// Creates a jail guard NPC with proper Schedule One component initialization
        /// </summary>
        /// <param name="position">World position to spawn the NPC</param>
        /// <param name="firstName">NPC first name</param>
        /// <param name="lastName">NPC last name</param>
        /// <returns>The created GameObject with all components properly initialized</returns>
        public static GameObject CreateJailGuard(Vector3 position, string firstName = "Officer", string lastName = "Smith")
        {
            return CreateNPC(NPCType.JailGuard, position, firstName, lastName);
        }

        /// <summary>
        /// Creates a parole officer NPC with proper Schedule One component initialization
        /// </summary>
        /// <param name="firstName">NPC first name</param>
        /// <param name="lastName">NPC last name</param>
        /// <returns>The created GameObject with all components properly initialized</returns>
        public static GameObject CreateParoleOfficer(Vector3 position, string firstName = "Officer", string lastName = "Johnson")
        {
            return CreateNPC(NPCType.ParoleOfficer, position, firstName, lastName);
        }

        /// <summary>
        /// Creates a jail inmate NPC with proper Schedule One component initialization
        /// </summary>
        /// <param name="position">World position to spawn the NPC</param>
        /// <param name="firstName">NPC first name</param>
        /// <param name="lastName">NPC last name</param>
        /// <returns>The created GameObject with all components properly initialized</returns>
        public static GameObject CreateJailInmate(Vector3 position, string firstName = "Inmate", string lastName = "Prisoner")
        {
            return CreateNPC(NPCType.JailInmate, position, firstName, lastName);
        }

        /// <summary>
        /// Creates a simple test NPC for debugging pathfinding
        /// </summary>
        /// <param name="name">NPC name</param>
        /// <param name="position">World position to spawn the NPC</param>
        /// <returns>The created GameObject with basic components for testing</returns>
        public static GameObject? CreateTestNPC(string name, Vector3 position)
        {
            ModLogger.Info($"Creating test NPC: {name} at position {position}");
            
            try
            {
                return CreateNPC(NPCType.TestNPC, position, name, "TestNPC");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Failed to create test NPC {name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Core NPC creation method that builds NPCs from scratch using Schedule One components
        /// </summary>
        /// <param name="npcType">Type of NPC to create</param>
        /// <param name="position">World position to spawn</param>
        /// <param name="firstName">First name</param>
        /// <param name="lastName">Last name</param>
        /// <returns>Fully configured NPC GameObject</returns>
        private static GameObject CreateNPC(NPCType npcType, Vector3 position, string firstName, string lastName)
        {
            try
            {
                ModLogger.Info($"Creating {npcType} NPC: {firstName} {lastName} at {position}");

                // 1. Create fresh GameObject - CRITICAL: SetActive(false) during setup
                GameObject npcObject = new GameObject($"{npcType}_{firstName}_{lastName}");
                npcObject.SetActive(false);
                npcObject.transform.position = position;

                // 2. Add Schedule One's core NPC component directly
#if !MONO
                var npcComponent = npcObject.AddComponent<Il2CppScheduleOne.NPCs.NPC>();
#else
                var npcComponent = npcObject.AddComponent<ScheduleOne.NPCs.NPC>();
#endif
                npcComponent.FirstName = firstName;
                npcComponent.LastName = lastName;
                npcComponent.ID = $"{npcType.ToString().ToLower()}_{System.Guid.NewGuid()}";

                // 3. Add physics components FIRST (this fixes floating issues)
                // TEMPORARILY DISABLED FOR TESTNPC - Rigidbody conflicts with NavMeshAgent
                if (npcType != NPCType.TestNPC)
                {
                    AddPhysicsComponents(npcObject);
                }
                else
                {
                    // TestNPC gets only NavMeshAgent, no Rigidbody/Collider conflicts
                    AddTestNPCNavigation(npcObject);
                }

                // 4. Add visual components and avatar FIRST (most important for appearance)
                AddVisualComponents(npcObject, npcComponent, npcType);

                // 5. Add Schedule One's health system (with proper component detection)
                AddHealthSystem(npcObject, npcComponent);

                // 6. Add messaging and dialogue systems
                AddMessagingSystem(npcObject, npcComponent);
                AddDialogueSystem(npcObject, npcComponent, npcType);

                // 6.5. Add voice and audio system
                AddAudioSystem(npcObject, npcComponent, npcType);

                // 7. Add basic interaction system only (skip complex networked components for now)
                AddBasicInteractionSystem(npcObject, npcComponent);

                // 10. Add jail-specific behavior LAST
                AddJailSpecificBehavior(npcObject, npcType);

                // 11. CRITICAL: Activate after everything is set up
                npcObject.SetActive(true);

                // 12. Add jail-specific behavior components for guards
                if (npcType == NPCType.JailGuard)
                {
                    // Add new GuardBehavior component instead of old GuardStateMachine
                    var guardBehavior = npcObject.AddComponent<GuardBehavior>();
                    // Note: Initialize method will be called by PrisonNPCManager with proper assignment

                    ModLogger.Debug($"✓ GuardBehavior component added to {firstName} {lastName}");
                }
                else if (npcType == NPCType.JailInmate)
                {
                    // Add basic inmate behavior (using BaseJailNPC)
                    var baseNPC = npcObject.GetComponent<BaseJailNPC>();
                    if (baseNPC != null)
                    {
                        // Configure as inmate
                        ModLogger.Debug($"✓ BaseJailNPC configured for inmate {firstName} {lastName}");
                    }
                }
                else if (npcType == NPCType.TestNPC)
                {
                    // Test NPC gets NO specialized behaviors - only basic movement
                    // Remove any existing behavior components that might have been added
                    var existingGuardBehavior = npcObject.GetComponent<GuardBehavior>();
                    if (existingGuardBehavior != null)
                    {
                        GameObject.DestroyImmediate(existingGuardBehavior);
                        ModLogger.Debug("Removed GuardBehavior from TestNPC");
                    }

                    // Keep BaseJailNPC for basic movement but disable complex behaviors
                    var baseNPC = npcObject.GetComponent<BaseJailNPC>();
                    if (baseNPC != null)
                    {
                        ModLogger.Debug("TestNPC will use BaseJailNPC for basic movement only");
                    }

                    ModLogger.Debug($"✓ Test NPC {firstName} ready for TestNPCController (clean of state machines)");
                }
                else if (npcType == NPCType.ParoleOfficer)
                {
                    // Add new ParoleOfficerBehavior component
                    var paroleBehavior = npcObject.AddComponent<ParoleOfficerBehavior>();
                    if (paroleBehavior != null)
                    {
                        // Add any essential initialization here
                        ModLogger.Debug($"✓ ParoleOfficerBehavior component added to {firstName} {lastName}");
                    }
                }

                    // 13. Initialize NavMesh positioning after activation
                    InitializeNavMeshPositioning(npcObject);

                ModLogger.Info($"✓ Successfully created {npcType} NPC: {firstName} {lastName}");
                return npcObject;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Failed to create {npcType} NPC: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Adds only NavMeshAgent for TestNPC to avoid Rigidbody conflicts
        /// </summary>
        private static void AddTestNPCNavigation(GameObject npc)
        {
            try
            {
                // NavMeshAgent ONLY - no Rigidbody or Collider interference
                var navAgent = npc.AddComponent<UnityEngine.AI.NavMeshAgent>();
                navAgent.height = 1.9f;     // Match NavMesh bake: 1.9
                navAgent.radius = 0.23f;    // Match NavMesh bake: 0.23 (EXACT from Unity screenshot)
                navAgent.speed = 3.5f; // Fast enough for stair climbing and navigation
                navAgent.stoppingDistance = 0.8f;
                navAgent.acceleration = 4f;
                navAgent.angularSpeed = 90f;
                
                // CRITICAL: Stair climbing settings - improved pathfinding
                navAgent.baseOffset = 0.1f; // Slightly elevate agent to avoid stairs collision issues
                
                // Enhanced obstacle avoidance for stairs and complex geometry
                navAgent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                navAgent.areaMask = -1; // Use all NavMesh areas
                navAgent.autoRepath = true;
                navAgent.autoBraking = true;
                navAgent.avoidancePriority = 50;
                
                // CRITICAL: Ensure NavMeshAgent controls position
                navAgent.updatePosition = true;
                navAgent.updateRotation = true;
                
                ModLogger.Debug("✓ TestNPC NavMeshAgent added (no Rigidbody conflicts)");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding TestNPC navigation: {e.Message}");
            }
        }

        /// <summary>
        /// Adds physics components in correct order to prevent floating issues
        /// </summary>
        private static void AddPhysicsComponents(GameObject npc)
        {
            try
            {
                // Rigidbody for physics (prevents floating)
                var rigidbody = npc.AddComponent<Rigidbody>();
                rigidbody.mass = 70f; // Average person weight
                rigidbody.freezeRotation = true; // Prevent tipping over
                rigidbody.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

                // Collider for collision detection
                var capsuleCollider = npc.AddComponent<CapsuleCollider>();
                capsuleCollider.height = 1.8f; // Average person height
                capsuleCollider.radius = 0.3f;
                capsuleCollider.center = new Vector3(0, 0.9f, 0); // Center at waist

                // NavMeshAgent for pathfinding - MUST match NavMesh bake settings exactly
                var navAgent = npc.AddComponent<UnityEngine.AI.NavMeshAgent>();
                navAgent.height = 1.9f;     // Match NavMesh bake: 1.9
                navAgent.radius = 0.23f;    // Match NavMesh bake: 0.23 (EXACT from Unity screenshot)
                navAgent.speed = 3.5f; // Fast enough for stair climbing and patrol movement
                navAgent.stoppingDistance = 0.8f; // Larger stopping distance for jail
                navAgent.acceleration = 4f; // Slower acceleration
                navAgent.angularSpeed = 90f; // Slower turning
                
                // CRITICAL: Stair climbing settings - improved pathfinding
                navAgent.baseOffset = 0.1f; // Slightly elevate agent to avoid stairs collision issues
                
                // Enhanced obstacle avoidance for stairs and complex geometry
                navAgent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.HighQualityObstacleAvoidance;
                navAgent.areaMask = -1; // Use all NavMesh areas
                navAgent.autoRepath = true;
                navAgent.autoBraking = true;
                navAgent.avoidancePriority = npc.name.Contains("Guard") ? 30 : 50; // Guards have higher priority

                ModLogger.Debug("✓ Physics components added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding physics components: {e.Message}");
            }
        }

        /// <summary>
        /// Links to the health system that NPC automatically gets (RequireComponent attribute)
        /// </summary>
        private static void AddHealthSystem(GameObject npc, object npcComponent)
        {
            try
            {
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted == null)
                {
                    ModLogger.Error($"NPC component is null, cannot link health system");
                    return;
                }

                // NPCHealth is automatically added via RequireComponent attribute on NPC class
                // We just need to find it and configure it
#if !MONO
                var healthComponent = npc.GetComponent<Il2CppScheduleOne.NPCs.NPCHealth>();
#else
                var healthComponent = npc.GetComponent<ScheduleOne.NPCs.NPCHealth>();
#endif

                if (healthComponent != null)
                {
                    // Configure the health component
                    healthComponent.MaxHealth = 100f;
                    healthComponent.Invincible = true; // Guards and prisoners shouldn't die easily in jail
                    
                    // Initialize health events if they don't exist
                    if (healthComponent.onDie == null)
                        healthComponent.onDie = new UnityEngine.Events.UnityEvent();
                    if (healthComponent.onKnockedOut == null)
                        healthComponent.onKnockedOut = new UnityEngine.Events.UnityEvent();

                    // Link to NPC
                    npc_casted.Health = healthComponent;
                    ModLogger.Debug($"✓ Found and configured health component for {npc.name}");
                }
                else
                {
                    ModLogger.Warn($"NPCHealth component not found on {npc.name} - RequireComponent should have added it");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error configuring health system: {e.Message}");
                ModLogger.Error($"Stack trace: {e.StackTrace}");
            }
        }

        /// <summary>
        /// Adds Schedule One's awareness system with proper child object hierarchy
        /// </summary>
        private static void AddAwarenessSystem(GameObject npc, object npcComponent)
        {
            try
            {
                // Create child object for awareness (like Schedule One does)
                GameObject awarenessObject = new GameObject("NPCAwareness");
                awarenessObject.transform.SetParent(npc.transform);

#if !MONO
                var awareness = awarenessObject.AddComponent<Il2CppScheduleOne.NPCs.NPCAwareness>();
                
                // Initialize awareness events
                awareness.onExplosionHeard = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.Noise.NoiseEvent>();
                awareness.onGunshotHeard = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.Noise.NoiseEvent>();
                awareness.onHitByCar = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.Vehicles.LandVehicle>();
                awareness.onNoticedDrugDealing = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedGeneralCrime = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedPettyCrime = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedPlayerViolatingCurfew = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedSuspiciousPlayer = new UnityEngine.Events.UnityEvent<Il2CppScheduleOne.PlayerScripts.Player>();
#else
                var awareness = awarenessObject.AddComponent<ScheduleOne.NPCs.NPCAwareness>();
                
                // Initialize awareness events
                awareness.onExplosionHeard = new UnityEngine.Events.UnityEvent<ScheduleOne.Noise.NoiseEvent>();
                awareness.onGunshotHeard = new UnityEngine.Events.UnityEvent<ScheduleOne.Noise.NoiseEvent>();
                awareness.onHitByCar = new UnityEngine.Events.UnityEvent<ScheduleOne.Vehicles.LandVehicle>();
                awareness.onNoticedDrugDealing = new UnityEngine.Events.UnityEvent<ScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedGeneralCrime = new UnityEngine.Events.UnityEvent<ScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedPettyCrime = new UnityEngine.Events.UnityEvent<ScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedPlayerViolatingCurfew = new UnityEngine.Events.UnityEvent<ScheduleOne.PlayerScripts.Player>();
                awareness.onNoticedSuspiciousPlayer = new UnityEngine.Events.UnityEvent<ScheduleOne.PlayerScripts.Player>();
#endif

                // Add listener component
#if !MONO
                awareness.Listener = npc.AddComponent<Il2CppScheduleOne.Noise.Listener>();
#else
                awareness.Listener = npc.AddComponent<ScheduleOne.Noise.Listener>();
#endif

                // Add vision system
                AddVisionSystem(npc, awareness);

                // Connect to main NPC
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif
                if (npc_casted != null)
                {
                    // Use reflection to set the awareness field
                    var awarenessField = npc_casted.GetType().GetField("awareness", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    awarenessField?.SetValue(npc_casted, awareness);
                }

                ModLogger.Debug("✓ Awareness system added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding awareness system: {e.Message}");
            }
        }

        /// <summary>
        /// Adds vision system for NPC awareness
        /// </summary>
        private static void AddVisionSystem(GameObject npc, object awareness)
        {
            try
            {
                // Vision cone object and behaviour
                GameObject visionObject = new GameObject("VisionCone");
                visionObject.transform.SetParent(npc.transform);
                
#if !MONO
                var visionCone = visionObject.AddComponent<Il2CppScheduleOne.Vision.VisionCone>();
                // Skip StatesOfInterest initialization for now due to PlayerVisualState access issues
                
                var awareness_casted = awareness as Il2CppScheduleOne.NPCs.NPCAwareness;
#else
                var visionCone = visionObject.AddComponent<ScheduleOne.Vision.VisionCone>();
                // Skip StatesOfInterest initialization for now due to PlayerVisualState access issues
                
                var awareness_casted = awareness as ScheduleOne.NPCs.NPCAwareness;
#endif

                if (awareness_casted != null)
                {
                    // Use reflection to set VisionCone
                    var visionField = awareness_casted.GetType().GetField("VisionCone", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    visionField?.SetValue(awareness_casted, visionCone);
                    
                    // Suspicious ? icon in world space
#if !MONO
                    awareness_casted.VisionCone.QuestionMarkPopup = npc.AddComponent<Il2CppScheduleOne.UI.WorldspacePopup.WorldspacePopup>();
#else
                    awareness_casted.VisionCone.QuestionMarkPopup = npc.AddComponent<ScheduleOne.UI.WorldspacePopup.WorldspacePopup>();
#endif
                }

                ModLogger.Debug("✓ Vision system added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding vision system: {e.Message}");
            }
        }

        /// <summary>
        /// Adds Schedule One's behavior system with proper hierarchy
        /// </summary>
        private static void AddBehaviorSystem(GameObject npc, object npcComponent)
        {
            try
            {
                // Create child object for behavior
                GameObject behaviourObject = new GameObject("NPCBehaviour");
                behaviourObject.transform.SetParent(npc.transform);

#if !MONO
                var behaviour = behaviourObject.AddComponent<Il2CppScheduleOne.NPCs.Behaviour.NPCBehaviour>();
                
                // Add cowering behavior
                GameObject coweringBehaviourObject = new GameObject("CoweringBehaviour");
                coweringBehaviourObject.transform.SetParent(behaviourObject.transform);
                var coweringBehaviour = coweringBehaviourObject.AddComponent<Il2CppScheduleOne.NPCs.Behaviour.CoweringBehaviour>();
                
                // Add flee behavior
                GameObject fleeBehaviourObject = new GameObject("FleeBehaviour");
                fleeBehaviourObject.transform.SetParent(behaviourObject.transform);
                var fleeBehaviour = fleeBehaviourObject.AddComponent<Il2CppScheduleOne.NPCs.Behaviour.FleeBehaviour>();
#else
                var behaviour = behaviourObject.AddComponent<ScheduleOne.NPCs.Behaviour.NPCBehaviour>();
                
                // Add cowering behavior
                GameObject coweringBehaviourObject = new GameObject("CoweringBehaviour");
                coweringBehaviourObject.transform.SetParent(behaviourObject.transform);
                var coweringBehaviour = coweringBehaviourObject.AddComponent<ScheduleOne.NPCs.Behaviour.CoweringBehaviour>();
                
                // Add flee behavior
                GameObject fleeBehaviourObject = new GameObject("FleeBehaviour");
                fleeBehaviourObject.transform.SetParent(behaviourObject.transform);
                var fleeBehaviour = fleeBehaviourObject.AddComponent<ScheduleOne.NPCs.Behaviour.FleeBehaviour>();
#endif

                behaviour.CoweringBehaviour = coweringBehaviour;
                behaviour.FleeBehaviour = fleeBehaviour;

                // Connect to main NPC
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif
                if (npc_casted != null)
                {
                    // Use reflection to set the behaviour field
                    var behaviourField = npc_casted.GetType().GetField("behaviour", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    behaviourField?.SetValue(npc_casted, behaviour);
                }

                ModLogger.Debug("✓ Behavior system added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding behavior system: {e.Message}");
            }
        }

        /// <summary>
        /// Adds Schedule One's interaction system
        /// </summary>
        private static void AddInteractionSystem(GameObject npc, object npcComponent)
        {
            try
            {
                // Add interaction component directly
#if !MONO
                var interactable = npc.AddComponent<Il2CppScheduleOne.Interaction.InteractableObject>();
#else
                var interactable = npc.AddComponent<ScheduleOne.Interaction.InteractableObject>();
#endif

                // Connect to main NPC (using reflection for cross-platform compatibility)
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif
                if (npc_casted != null)
                {
                    // Use reflection to set private field
                    var intObjField = npc_casted.GetType().GetField("intObj", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    intObjField?.SetValue(npc_casted, interactable);
                }

                ModLogger.Debug("✓ Interaction system added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding interaction system: {e.Message}");
            }
        }

        /// <summary>
        /// Adds Schedule One's messaging and conversation system
        /// </summary>
        private static void AddMessagingSystem(GameObject npc, object npcComponent)
        {
            try
            {
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null)
                {
                    // Set conversation categories
#if !MONO
                    npc_casted.ConversationCategories = new Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Messaging.EConversationCategory>();
                    npc_casted.ConversationCategories.Add(Il2CppScheduleOne.Messaging.EConversationCategory.Customer);
#else
                    npc_casted.ConversationCategories = new System.Collections.Generic.List<ScheduleOne.Messaging.EConversationCategory>();
                    npc_casted.ConversationCategories.Add(ScheduleOne.Messaging.EConversationCategory.Customer);
#endif

                    // Create message conversation
#if !MONO
                    npc_casted.CreateMessageConversation();
#else
                    var createConvoMethod = AccessTools.Method(typeof(ScheduleOne.NPCs.NPC), "CreateMessageConversation");
                    createConvoMethod?.Invoke(npc_casted, null);
#endif
                }

                ModLogger.Debug("✓ Messaging system added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding messaging system: {e.Message}");
            }
        }

        /// <summary>
        /// Adds DialogueHandler and DialogueController components for custom NPC conversations
        /// </summary>
        private static void AddDialogueSystem(GameObject npc, object npcComponent, NPCType npcType)
        {
            try
            {
#if !MONO
                var npc_casted = npcComponent as Il2CppScheduleOne.NPCs.NPC;
#else
                var npc_casted = npcComponent as ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null)
                {
                    // Add DialogueHandler component
#if !MONO
                    var dialogueHandler = npc.AddComponent<Il2CppScheduleOne.Dialogue.DialogueHandler>();
#else
                    var dialogueHandler = npc.AddComponent<ScheduleOne.Dialogue.DialogueHandler>();
#endif

                    // Add DialogueController component
#if !MONO
                    var dialogueController = npc.AddComponent<Il2CppScheduleOne.Dialogue.DialogueController>();
#else
                    var dialogueController = npc.AddComponent<ScheduleOne.Dialogue.DialogueController>();
#endif

                    // Configure basic dialogue settings
                    dialogueController.DialogueEnabled = true;
                    dialogueController.UseDialogueBehaviour = true;

                    // Add our custom JailNPCDialogueController for enhanced functionality
                    var jailDialogueController = npc.AddComponent<JailNPCDialogueController>();

                    // Set up default greetings based on NPC type
                    SetupNPCDialogueByType(jailDialogueController, npcType);

                    ModLogger.Debug($"✓ Dialogue system added for {npcType} NPC");
                }
                else
                {
                    ModLogger.Error("Could not cast NPC component for dialogue system setup");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding dialogue system: {e.Message}");
            }
        }

        /// <summary>
        /// Configure dialogue based on NPC type
        /// </summary>
        private static void SetupNPCDialogueByType(JailNPCDialogueController dialogueController, NPCType npcType)
        {
            try
            {
                switch (npcType)
                {
                    case NPCType.JailGuard:
                        dialogueController.AddStateDialogue("Idle", "Everything's secure here.",
                            new[] { "Move along.", "Keep it quiet.", "No trouble here." });
                        dialogueController.AddStateDialogue("Patrolling", "I'm on patrol.",
                            new[] { "Stay out of restricted areas.", "Keep moving.", "No loitering." });
                        dialogueController.AddStateDialogue("Alert", "Something's not right.",
                            new[] { "Stop right there!", "What are you doing?", "Explain yourself!" });
                        dialogueController.AddStateDialogue("Processing", "Time to process you.",
                            new[] { "Follow me to your cell.", "This way.", "Stay close." });
                        dialogueController.AddStateDialogue("Escorting", "Follow me.",
                            new[] { "Keep moving.", "Stay close.", "This way." });
                        break;

                    case NPCType.GenericJailStaff:
                        dialogueController.AddStateDialogue("Idle", "I'm here to process inmates.",
                            new[] { "What do you need?", "State your business.", "I'm busy with paperwork." });
                        dialogueController.AddStateDialogue("Processing", "Time to process you.",
                            new[] { "Follow me to your cell.", "This way.", "Stay close." });
                        dialogueController.AddStateDialogue("Escorting", "Follow me.",
                            new[] { "Keep moving.", "Stay close.", "This way." });
                        break;

                    case NPCType.TestNPC:
                        dialogueController.AddStateDialogue("Idle", "Hello there.",
                            new[] { "Nice weather today.", "How are you?", "Good to see you." });
                        break;

                    default:
                        dialogueController.AddStateDialogue("Idle", "Hello.",
                            new[] { "Hi there.", "Good day.", "What do you need?" });
                        break;
                }

                ModLogger.Debug($"✓ Dialogue configured for {npcType} NPC");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error setting up dialogue for {npcType}: {e.Message}");
            }
        }

        /// <summary>
        /// Adds visual components and avatar system
        /// </summary>
        private static void AddVisualComponents(GameObject npc, object npcComponent, NPCType npcType)
        {
            try
            {
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null)
                {
                    // Generate unique GUID for save system
                    npc_casted.BakedGUID = System.Guid.NewGuid().ToString();

                    // Add NPC inventory component
                    AddInventorySystem(npc, npc_casted);
                    
                    // Create proper avatar system
                    CreateProperAvatar(npc, npc_casted, npcType);
                }

                ModLogger.Debug("✓ Visual components added successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding visual components: {e.Message}");
            }
        }

        /// <summary>
        /// Creates a proper multi-part avatar system like the player has (12+ components)
        /// </summary>
        private static void CreateProperAvatar(GameObject npc, object npcComponent, NPCType npcType)
        {
            try
            {
                ModLogger.Info($"Creating full avatar structure for {npc.name}");

                // Try to find an existing NPC with a working avatar to copy from (use original working method)
                var existingNPC = FindExistingNPCWithAvatar();
                if (existingNPC != null)
                {
                    CopyAvatarStructure(existingNPC, npc, npcComponent);
                    return;
                }

                // Fallback: Try to find the player's avatar structure
                var player = UnityEngine.Object.FindObjectOfType<
#if !MONO
                    Il2CppScheduleOne.PlayerScripts.Player
#else
                    ScheduleOne.PlayerScripts.Player
#endif
                >();

                if (player != null && player.Avatar != null)
                {
                    ModLogger.Info("Found player avatar, copying structure");
                    CopyAvatarStructure(player.gameObject, npc, npcComponent);
                    return;
                }

                // Final fallback: Create basic avatar
                CreateBasicAvatar(npc, npcComponent);
                
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating proper avatar: {e.Message}");
                // Fallback to basic visual
                AddBasicVisualMesh(npc);
            }
        }

        /// <summary>
        /// Find an existing NPC in the scene that has a working avatar, with preference for specific types
        /// </summary>
        private static GameObject FindExistingNPCWithAvatar(NPCType? preferredType = null)
        {
            var existingNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                Il2CppScheduleOne.NPCs.NPC
#else
                ScheduleOne.NPCs.NPC
#endif
            >();

            // Define preferred avatar sources based on NPC type
            string[] preferredNames = new string[0];
            if (preferredType.HasValue)
            {
                switch (preferredType.Value)
                {
                    case NPCType.JailInmate:
                    case NPCType.TestNPC:
                        preferredNames = new[] { "billy", "kramer", "billy_kramer", "inmate" };
                        break;
                        
                    case NPCType.JailGuard:
                        preferredNames = new[] { "officerbailey", "officercooper", "officergreen", "officerhoward",
                                               "officerjackson", "officerlee", "officerlopez", "officermurphy", 
                                               "officeroakley", "officer", "police", "cop" };
                        break;
                }
            }

            // First pass: Look for preferred avatar sources
            if (preferredNames.Length > 0)
            {
                foreach (var preferredName in preferredNames)
                {
                    foreach (var existingNPC in existingNPCs)
                    {
                        // Skip our own NPCs
                        if (existingNPC.gameObject.name.Contains("JailGuard") || 
                            existingNPC.gameObject.name.Contains("JailInmate"))
                            continue;

                        if (existingNPC.Avatar != null && 
                            existingNPC.gameObject.name.ToLower().Contains(preferredName.ToLower()))
                        {
                            ModLogger.Info($"Found preferred NPC avatar source: {existingNPC.name} for {preferredType}");
                            return existingNPC.gameObject;
                        }
                    }
                }
            }

            // Second pass: Any NPC with avatar (fallback)
            foreach (var existingNPC in existingNPCs)
            {
                // Skip our own NPCs
                if (existingNPC.gameObject.name.Contains("JailGuard") || 
                    existingNPC.gameObject.name.Contains("JailInmate"))
                    continue;

                if (existingNPC.Avatar != null)
                {
                    ModLogger.Info($"Found fallback NPC with avatar: {existingNPC.name}");
                    return existingNPC.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Simple method to find any existing NPC with avatar (original working version)
        /// </summary>
        private static GameObject FindExistingNPCWithAvatar()
        {
            var existingNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                Il2CppScheduleOne.NPCs.NPC
#else
                ScheduleOne.NPCs.NPC
#endif
            >();

            ModLogger.Info($"Searching through {existingNPCs.Length} existing NPCs for Billy...");

            // Log all available NPCs first
            foreach (var npc in existingNPCs)
            {
                if (!npc.gameObject.name.Contains("JailGuard") && 
                    !npc.gameObject.name.Contains("JailInmate") &&
                    !npc.gameObject.name.Contains("TestNPC"))
                {
                    // Check for avatar using the same method as CopyAvatarStructure
                    bool hasAvatar = false;
                    var avatar = npc.gameObject.GetComponentInChildren<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                    if (avatar == null)
                    {
                        avatar = npc.gameObject.GetComponent<
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.Avatar
#else
                            ScheduleOne.AvatarFramework.Avatar
#endif
                        >();
                    }
                    if (avatar == null)
                    {
                        Transform avatarChild = npc.transform.Find("Avatar");
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
                    hasAvatar = avatar != null;
                    
                    //ModLogger.Info($"Available NPC: {npc.name} (Avatar: {(hasAvatar ? "✓" : "✗")})");
                }
            }

            // First try to find Billy specifically
            foreach (var existingNPC in existingNPCs)
            {
                // Skip our own NPCs
                if (existingNPC.gameObject.name.Contains("JailGuard") || 
                    existingNPC.gameObject.name.Contains("JailInmate") ||
                    existingNPC.gameObject.name.Contains("TestNPC"))
                    continue;

                // Look for Billy first
                if (existingNPC.name.ToLower().Contains("billy"))
                {
                    // Check for avatar using the same comprehensive method
                    var avatar = existingNPC.gameObject.GetComponentInChildren<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                    if (avatar == null)
                    {
                        avatar = existingNPC.gameObject.GetComponent<
#if !MONO
                            Il2CppScheduleOne.AvatarFramework.Avatar
#else
                            ScheduleOne.AvatarFramework.Avatar
#endif
                        >();
                    }
                    if (avatar == null)
                    {
                        Transform avatarChild = existingNPC.transform.Find("Avatar");
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
                    
                    if (avatar != null)
                    {
                        ModLogger.Info($"Found Billy with avatar: {existingNPC.name}");
                        return existingNPC.gameObject;
                    }
                }
            }

            // If no Billy found, fall back to any NPC with avatar
            foreach (var existingNPC in existingNPCs)
            {
                // Skip our own NPCs
                if (existingNPC.gameObject.name.Contains("JailGuard") || 
                    existingNPC.gameObject.name.Contains("JailInmate") ||
                    existingNPC.gameObject.name.Contains("TestNPC"))
                    continue;

                // Check for avatar using the same comprehensive method
                var avatar = existingNPC.gameObject.GetComponentInChildren<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Avatar
#else
                    ScheduleOne.AvatarFramework.Avatar
#endif
                >();
                if (avatar == null)
                {
                    avatar = existingNPC.gameObject.GetComponent<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                }
                if (avatar == null)
                {
                    Transform avatarChild = existingNPC.transform.Find("Avatar");
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

                if (avatar != null)
                {
                    ModLogger.Info($"Billy not found, using fallback NPC with avatar: {existingNPC.name}");
                    return existingNPC.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Copy the complete avatar structure from a source NPC/Player
        /// </summary>
        private static void CopyAvatarStructure(GameObject source, GameObject target, object npcComponent)
        {
            try
            {
                ModLogger.Info($"Attempting to copy avatar from {source.name} to {target.name}");

                // Try multiple ways to find the avatar component
                var sourceAvatar = source.GetComponentInChildren<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Avatar
#else
                    ScheduleOne.AvatarFramework.Avatar
#endif
                >();

                // If not found in children, try direct component
                if (sourceAvatar == null)
                {
                    sourceAvatar = source.GetComponent<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                }

                // Also check if there's an "Avatar" child GameObject
                Transform avatarChild = source.transform.Find("Avatar");
                if (sourceAvatar == null && avatarChild != null)
                {
                    sourceAvatar = avatarChild.GetComponent<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Avatar
#else
                        ScheduleOne.AvatarFramework.Avatar
#endif
                    >();
                }

                if (sourceAvatar == null)
                {
                    ModLogger.Warn($"Source {source.name} doesn't have an Avatar component (checked component, children, and Avatar child)");
                    CreateBasicAvatar(target, npcComponent);
                    return;
                }

                ModLogger.Info($"Found avatar component on {sourceAvatar.gameObject.name} (parent: {source.name})");

                // Copy the entire avatar GameObject hierarchy
                GameObject avatarCopy = UnityEngine.Object.Instantiate(sourceAvatar.gameObject);
                avatarCopy.name = "Avatar";
                avatarCopy.transform.SetParent(target.transform);
                avatarCopy.transform.localPosition = Vector3.zero;
                avatarCopy.transform.localRotation = Quaternion.identity;
                avatarCopy.transform.localScale = Vector3.one;

                // Get the copied avatar component
                var copiedAvatar = avatarCopy.GetComponent<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Avatar
#else
                    ScheduleOne.AvatarFramework.Avatar
#endif
                >();

                // Link to NPC
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null && copiedAvatar != null)
                {
                    npc_casted.Avatar = copiedAvatar;
                    
                    // Enable the main avatar GameObject
                    avatarCopy.SetActive(true);
                    
                    // RADICAL APPROACH: Just add the TestNPCController that we know works
                    var testController = target.AddComponent<TestNPCController>();
                    testController.usePatrolMode = true; // Use patrol mode for prison NPCs
                    ModLogger.Info($"✓ Added working TestNPCController to {target.name} for guaranteed animations");
                    
                    ModLogger.Info($"✓ Copied and enabled complete avatar structure to {target.name} ({avatarCopy.transform.childCount} child objects)");
                }
                else
                {
                    ModLogger.Warn($"Could not link copied avatar - npc: {npc_casted != null}, avatar: {copiedAvatar != null}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error copying avatar structure: {e.Message}");
                CreateBasicAvatar(target, npcComponent);
            }
        }

        /// <summary>
        /// Create a basic avatar when we can't copy from existing sources
        /// </summary>
        private static void CreateBasicAvatar(GameObject npc, object npcComponent)
        {
            try
            {
                // Create an Avatar GameObject as a child
                GameObject avatarObject = new GameObject("Avatar");
                avatarObject.transform.SetParent(npc.transform);
                avatarObject.transform.localPosition = Vector3.zero;
                avatarObject.transform.localRotation = Quaternion.identity;

                // Add the Avatar component
#if !MONO
                var avatarComponent = avatarObject.AddComponent<Il2CppScheduleOne.AvatarFramework.Avatar>();
#else
                var avatarComponent = avatarObject.AddComponent<ScheduleOne.AvatarFramework.Avatar>();
#endif

                // Link to NPC
                var npc_casted = npcComponent as 
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC;
#else
                    ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null && avatarComponent != null)
                {
                    npc_casted.Avatar = avatarComponent;
                    ModLogger.Info($"✓ Created basic avatar for {npc.name}");
                }

                // Add basic visual mesh as fallback
                AddBasicVisualMesh(npc);
                
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating basic avatar: {e.Message}");
                AddBasicVisualMesh(npc);
            }
        }

        /// <summary>
        /// Creates default avatar settings for jail NPCs
        /// </summary>
        private static object CreateDefaultAvatarSettings(string npcName)
        {
            try
            {
                // Try to find existing avatar settings from other NPCs
                var existingNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();

                foreach (var existingNPC in existingNPCs)
                {
                    // Skip our own NPCs
                    if (existingNPC.gameObject.name.Contains("JailGuard") || 
                        existingNPC.gameObject.name.Contains("JailInmate"))
                        continue;

                    if (existingNPC.Avatar != null && existingNPC.Avatar.CurrentSettings != null)
                    {
                        ModLogger.Info($"✓ Found avatar settings from {existingNPC.name} for {npcName}");
                        return existingNPC.Avatar.CurrentSettings;
                    }
                }

                // If no existing settings found, create basic default settings
                ModLogger.Info($"Creating default avatar settings for {npcName}");
                
#if !MONO
                var settings = ScriptableObject.CreateInstance<Il2CppScheduleOne.AvatarFramework.AvatarSettings>();
#else
                var settings = ScriptableObject.CreateInstance<ScheduleOne.AvatarFramework.AvatarSettings>();
#endif

                // Set basic appearance
                if (npcName.Contains("Guard"))
                {
                    // Guard appearance
                    settings.SkinColor = new Color(0.9f, 0.8f, 0.7f); // Light skin
                    settings.Height = 1.0f;
                    settings.Gender = 0.8f; // More masculine
                    settings.Weight = 0.3f; // Average build
                    settings.HairColor = new Color(0.4f, 0.2f, 0.1f); // Brown color
                }
                else if (npcName.Contains("Inmate"))
                {
                    // Inmate appearance
                    settings.SkinColor = new Color(0.8f, 0.7f, 0.6f); // Slightly darker skin
                    settings.Height = 0.9f;
                    settings.Gender = 0.7f; // Masculine
                    settings.Weight = 0.4f; // Slightly heavier
                    settings.HairColor = Color.black;
                }

                return settings;
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error creating default avatar settings: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Adds inventory system for NPCs
        /// </summary>
        private static void AddInventorySystem(GameObject npc, object npcComponent)
        {
            try
            {
#if !MONO
                var inventory = npc.AddComponent<Il2CppScheduleOne.NPCs.NPCInventory>();
                inventory.PickpocketIntObj = npc.AddComponent<Il2CppScheduleOne.Interaction.InteractableObject>();
#else
                var inventory = npc.AddComponent<ScheduleOne.NPCs.NPCInventory>();
                inventory.PickpocketIntObj = npc.AddComponent<ScheduleOne.Interaction.InteractableObject>();
#endif

                ModLogger.Debug("✓ Inventory system added");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding inventory system: {e.Message}");
            }
        }

        /// <summary>
        /// Attempts to clone an existing NPC's visual representation
        /// </summary>
        private static void AddNPCModel(GameObject npc)
        {
            try
            {
                // Try to find an existing NPC in the scene to copy visual from
                var existingNPCs = UnityEngine.Object.FindObjectsOfType<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPC
#else
                    ScheduleOne.NPCs.NPC
#endif
                >();

                GameObject sourceNPC = null;
                foreach (var existingNPC in existingNPCs)
                {
                    // Skip our own NPCs
                    if (existingNPC.gameObject.name.Contains("JailGuard") || 
                        existingNPC.gameObject.name.Contains("JailInmate"))
                        continue;

                    sourceNPC = existingNPC.gameObject;
                    break;
                }

                if (sourceNPC != null)
                {
                    // Copy visual components from existing NPC
                    CopyVisualComponents(sourceNPC, npc);
                    ModLogger.Debug($"✓ Copied visual components from {sourceNPC.name}");
                }
                else
                {
                    // Fallback to basic representation
                    AddBasicVisualMesh(npc);
                    ModLogger.Debug("✓ Added fallback visual representation");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding NPC model: {e.Message}");
                // Fallback to basic mesh
                AddBasicVisualMesh(npc);
            }
        }

        /// <summary>
        /// Copies visual components from source NPC to target NPC
        /// </summary>
        private static void CopyVisualComponents(GameObject sourceNPC, GameObject targetNPC)
        {
            try
            {
                // Find all renderers in the source NPC
                var sourceRenderers = sourceNPC.GetComponentsInChildren<MeshRenderer>();
                var sourceMeshFilters = sourceNPC.GetComponentsInChildren<MeshFilter>();
                var sourceSkinnedRenderers = sourceNPC.GetComponentsInChildren<SkinnedMeshRenderer>();

                // Copy MeshRenderer components
                for (int i = 0; i < sourceRenderers.Length && i < sourceMeshFilters.Length; i++)
                {
                    var sourceRenderer = sourceRenderers[i];
                    var sourceMeshFilter = sourceMeshFilters[i];

                    if (sourceRenderer != null && sourceMeshFilter != null && sourceMeshFilter.sharedMesh != null)
                    {
                        // Create child object for this mesh part
                        GameObject meshPart = new GameObject($"MeshPart_{i}");
                        meshPart.transform.SetParent(targetNPC.transform);
                        meshPart.transform.localPosition = sourceRenderer.transform.localPosition;
                        meshPart.transform.localRotation = sourceRenderer.transform.localRotation;
                        meshPart.transform.localScale = sourceRenderer.transform.localScale;

                        var newMeshFilter = meshPart.AddComponent<MeshFilter>();
                        var newRenderer = meshPart.AddComponent<MeshRenderer>();

                        newMeshFilter.sharedMesh = sourceMeshFilter.sharedMesh;
                        newRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                    }
                }

                // Copy SkinnedMeshRenderer components
                foreach (var sourceSkinnedRenderer in sourceSkinnedRenderers)
                {
                    if (sourceSkinnedRenderer != null && sourceSkinnedRenderer.sharedMesh != null)
                    {
                        GameObject skinnedPart = new GameObject($"SkinnedMeshPart");
                        skinnedPart.transform.SetParent(targetNPC.transform);
                        skinnedPart.transform.localPosition = sourceSkinnedRenderer.transform.localPosition;
                        skinnedPart.transform.localRotation = sourceSkinnedRenderer.transform.localRotation;
                        skinnedPart.transform.localScale = sourceSkinnedRenderer.transform.localScale;

                        var newSkinnedRenderer = skinnedPart.AddComponent<SkinnedMeshRenderer>();
                        newSkinnedRenderer.sharedMesh = sourceSkinnedRenderer.sharedMesh;
                        newSkinnedRenderer.sharedMaterials = sourceSkinnedRenderer.sharedMaterials;
                        
                        // Try to copy bone structure (simplified)
                        if (sourceSkinnedRenderer.bones != null && sourceSkinnedRenderer.bones.Length > 0)
                        {
                            newSkinnedRenderer.bones = sourceSkinnedRenderer.bones;
                            newSkinnedRenderer.rootBone = sourceSkinnedRenderer.rootBone;
                        }
                    }
                }

                ModLogger.Debug("✓ Visual components copied successfully");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error copying visual components: {e.Message}");
                // Fallback to basic mesh
                AddBasicVisualMesh(targetNPC);
            }
        }

        /// <summary>
        /// Adds basic visual mesh representation as fallback
        /// </summary>
        private static void AddBasicVisualMesh(GameObject npc)
        {
            try
            {
                // Create basic capsule mesh for visual representation
                var meshFilter = npc.AddComponent<MeshFilter>();
                var meshRenderer = npc.AddComponent<MeshRenderer>();

                // Create a basic capsule mesh
                GameObject tempCapsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                meshFilter.mesh = tempCapsule.GetComponent<MeshFilter>().mesh;
                UnityEngine.Object.DestroyImmediate(tempCapsule);

                // Create basic material with different colors for different NPC types
                var material = new Material(Shader.Find("Standard"));
                
                // Color based on NPC type (determined from name)
                if (npc.name.Contains("JailGuard"))
                {
                    material.color = Color.blue; // Blue for guards
                }
                else if (npc.name.Contains("JailInmate"))
                {
                    material.color = new Color(1f, 0.5f, 0f); // Orange for inmates
                }
                else
                {
                    material.color = Color.cyan; // Cyan for other NPCs
                }

                meshRenderer.material = material;

                ModLogger.Debug("✓ Basic visual mesh added");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding basic visual mesh: {e.Message}");
            }
        }

        /// <summary>
        /// Adds jail-specific behavior components based on NPC type
        /// Note: The actual behavior components will be added after NPC creation in Core.cs
        /// This is just a placeholder for future behavior additions
        /// </summary>
        private static void AddJailSpecificBehavior(GameObject npc, NPCType npcType)
        {
            try
            {
                // Mark the NPC with the type for later behavior assignment
                // The actual JailGuardBehavior and JailInmateBehavior components 
                // will be added in Core.cs after the NPC is fully created
                npc.name += $"_{npcType}";

                ModLogger.Debug($"✓ NPC marked as {npcType} for behavior assignment");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error marking NPC for jail-specific behavior: {e.Message}");
            }
        }

        /// <summary>
        /// Adds basic interaction system without complex dependencies
        /// </summary>
        private static void AddBasicInteractionSystem(GameObject npc, object npcComponent)
        {
            try
            {
                // Add basic interactable object component
#if !MONO
                var interactable = npc.AddComponent<Il2CppScheduleOne.Interaction.InteractableObject>();
#else
                var interactable = npc.AddComponent<ScheduleOne.Interaction.InteractableObject>();
#endif

                if (interactable != null)
                {
                    ModLogger.Debug($"✓ Basic interaction system added to {npc.name}");
                }
                else
                {
                    ModLogger.Warn($"Could not add InteractableObject to {npc.name}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding basic interaction system: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize NavMesh positioning and ensure NPC is on the NavMesh
        /// </summary>
        private static void InitializeNavMeshPositioning(GameObject npc)
        {
            try
            {
                var navAgent = npc.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (navAgent == null)
                {
                    ModLogger.Warn($"No NavMeshAgent found on {npc.name}");
                    return;
                }

                // Wait a frame for NavMesh to initialize properly
                MelonLoader.MelonCoroutines.Start(InitializeNavMeshCoroutine(npc, navAgent));
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing NavMesh positioning for {npc.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Coroutine to ensure NPC is properly positioned on NavMesh
        /// </summary>
        private static System.Collections.IEnumerator InitializeNavMeshCoroutine(GameObject npc, UnityEngine.AI.NavMeshAgent navAgent)
        {
            // Wait a frame for everything to initialize
            yield return null;

            try
            {
                Vector3 currentPos = npc.transform.position;
                
                // Try to find the nearest NavMesh position
                if (UnityEngine.AI.NavMesh.SamplePosition(currentPos, out UnityEngine.AI.NavMeshHit hit, 5.0f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    // Warp to the nearest NavMesh position
                    navAgent.Warp(hit.position);
                    ModLogger.Info($"✓ Positioned {npc.name} on NavMesh at {hit.position}");
                    
                    // Enable the NavMeshAgent
                    navAgent.enabled = true;
                    
                    // Start basic patrol behavior (except for TestNPC and guards with GuardBehavior)
                    if (!npc.name.Contains("TestNPC") && npc.GetComponent<GuardBehavior>() == null)
                    {
                        MelonLoader.MelonCoroutines.Start(BasicPatrolBehavior(npc, navAgent));
                    }
                    else if (npc.GetComponent<GuardBehavior>() != null)
                    {
                        ModLogger.Info($"Skipped BasicPatrolBehavior for JailGuard with GuardBehavior: {npc.name}");
                    }
                    else
                    {
                        ModLogger.Info($"Skipped BasicPatrolBehavior for TestNPC: {npc.name}");
                    }
                }
                else
                {
                    ModLogger.Warn($"Could not find NavMesh near {npc.name} at position {currentPos}");
                    
                    // Try to find any NavMesh position in a larger radius
                    if (UnityEngine.AI.NavMesh.SamplePosition(currentPos, out hit, 20.0f, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        navAgent.Warp(hit.position);
                        navAgent.enabled = true;
                        ModLogger.Info($"✓ Positioned {npc.name} on distant NavMesh at {hit.position}");
                        
                        // Start basic patrol behavior (except for TestNPC and guards with GuardBehavior)
                        if (!npc.name.Contains("TestNPC") && npc.GetComponent<GuardBehavior>() == null)
                        {
                            MelonLoader.MelonCoroutines.Start(BasicPatrolBehavior(npc, navAgent));
                        }
                        else if (npc.GetComponent<GuardBehavior>() != null)
                        {
                            ModLogger.Info($"Skipped BasicPatrolBehavior for distant NavMesh JailGuard: {npc.name}");
                        }
                        else
                        {
                            ModLogger.Info($"Skipped BasicPatrolBehavior for distant NavMesh TestNPC: {npc.name}");
                        }
                    }
                    else
                    {
                        ModLogger.Error($"No NavMesh found anywhere near {npc.name} - disabling NavMeshAgent");
                        navAgent.enabled = false;
                    }
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error in NavMesh initialization coroutine for {npc.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Basic patrol behavior for NPCs using NavMesh
        /// </summary>
        private static System.Collections.IEnumerator BasicPatrolBehavior(GameObject npc, UnityEngine.AI.NavMeshAgent navAgent)
        {
            if (navAgent == null || !navAgent.enabled) yield break;

            Vector3 originalPosition = npc.transform.position;
            float patrolRadius = npc.name.Contains("Guard") ? 8.0f : 5.0f; // Guards patrol wider areas
            float waitTime = npc.name.Contains("Inmate") ? 10.0f : 6.0f; // Inmates move less frequently

            while (navAgent != null && navAgent.enabled && npc != null)
            {
                bool hasError = false;
                
                // Find a random point within patrol radius
                Vector3 randomDirection = UnityEngine.Random.insideUnitSphere * patrolRadius;
                randomDirection += originalPosition;
                randomDirection.y = originalPosition.y; // Keep same height

                try
                {
                    // Find nearest NavMesh position to the random point
                    if (UnityEngine.AI.NavMesh.SamplePosition(randomDirection, out UnityEngine.AI.NavMeshHit hit, patrolRadius, UnityEngine.AI.NavMesh.AllAreas))
                    {
                        navAgent.SetDestination(hit.position);
                        //ModLogger.Debug($"{npc.name} patrolling to {hit.position}");
                    }
                }
                catch (Exception e)
                {
                    ModLogger.Error($"Error in patrol behavior for {npc.name}: {e.Message}");
                    hasError = true;
                }

                // Wait for agent to reach destination (outside try-catch)
                if (!hasError && navAgent != null && navAgent.hasPath)
                {
                    float timeout = 15.0f;
                    while (navAgent.pathPending || (navAgent.remainingDistance > 0.5f && timeout > 0))
                    {
                        yield return new UnityEngine.WaitForSeconds(0.5f);
                        timeout -= 0.5f;
                    }
                }

                // Wait at destination (outside try-catch)
                if (hasError)
                {
                    yield return new UnityEngine.WaitForSeconds(5f);
                }
                else
                {
                    yield return new UnityEngine.WaitForSeconds(waitTime + UnityEngine.Random.Range(0f, 5f));
                }
            }
        }

        /// <summary>
        /// Add NPCAnimation component which handles movement animation
        /// </summary>
        private static void AddNPCAnimationComponent(GameObject npc, 
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar avatar,
            Il2CppScheduleOne.NPCs.NPC npcComponent
#else
            ScheduleOne.AvatarFramework.Avatar avatar,
            ScheduleOne.NPCs.NPC npcComponent
#endif
        )
        {
            try
            {
                // Check if NPCAnimation already exists
                var existingNPCAnimation = npc.GetComponent<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPCAnimation
#else
                    ScheduleOne.NPCs.NPCAnimation
#endif
                >();

                if (existingNPCAnimation == null)
                {
                    // Add NPCAnimation component
                    var npcAnimation = npc.AddComponent<
#if !MONO
                        Il2CppScheduleOne.NPCs.NPCAnimation
#else
                        ScheduleOne.NPCs.NPCAnimation
#endif
                    >();

                    // Configure all required references for NPCAnimation
                    ConfigureNPCAnimationReferences(npc, npcAnimation, avatar, npcComponent);
                    
                    ModLogger.Info($"✓ NPCAnimation component added and configured for {npc.name}");
                }
                else
                {
                    // Update existing component references
                    ConfigureNPCAnimationReferences(npc, existingNPCAnimation, avatar, npcComponent);
                    ModLogger.Info($"✓ Updated existing NPCAnimation on {npc.name}");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding NPCAnimation component: {e.Message}");
            }
        }

        /// <summary>
        /// Configure all references needed for NPCAnimation to work properly
        /// </summary>
        private static void ConfigureNPCAnimationReferences(GameObject npc,
#if !MONO
            Il2CppScheduleOne.NPCs.NPCAnimation npcAnimation,
            Il2CppScheduleOne.AvatarFramework.Avatar avatar,
            Il2CppScheduleOne.NPCs.NPC npcComponent
#else
            ScheduleOne.NPCs.NPCAnimation npcAnimation,
            ScheduleOne.AvatarFramework.Avatar avatar,
            ScheduleOne.NPCs.NPC npcComponent
#endif
        )
        {
            try
            {
                // Set Avatar reference
                npcAnimation.npc.Avatar = avatar;

                // Find AvatarAnimation component (should be on avatar GameObject)
                var avatarAnimation = avatar.GetComponent<
#if !MONO
                    Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation
#else
                    ScheduleOne.AvatarFramework.Animation.AvatarAnimation
#endif
                >();

                if (avatarAnimation == null)
                {
                    avatarAnimation = avatar.GetComponentInChildren<
#if !MONO
                        Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation
#else
                        ScheduleOne.AvatarFramework.Animation.AvatarAnimation
#endif
                    >();
                }

                // Find SmoothedVelocityCalculator component
                var velocityCalculator = avatar.GetComponent<
#if !MONO
                    Il2CppScheduleOne.Tools.SmoothedVelocityCalculator
#else
                    ScheduleOne.Tools.SmoothedVelocityCalculator
#endif
                >();

                if (velocityCalculator == null)
                {
                    velocityCalculator = avatar.GetComponentInChildren<
#if !MONO
                        Il2CppScheduleOne.Tools.SmoothedVelocityCalculator
#else
                        ScheduleOne.Tools.SmoothedVelocityCalculator
#endif
                    >();
                }

                // Find NPCMovement component
                var npcMovement = npc.GetComponent<
#if !MONO
                    Il2CppScheduleOne.NPCs.NPCMovement
#else
                    ScheduleOne.NPCs.NPCMovement
#endif
                >();

                // Use reflection to set private fields since they're protected/private
                var npcAnimationType = npcAnimation.GetType();
                
                // Set anim field
                if (avatarAnimation != null)
                {
                    var animField = npcAnimationType.GetField("anim", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    animField?.SetValue(npcAnimation, avatarAnimation);
                    ModLogger.Debug($"✓ Set AvatarAnimation reference for {npc.name}");
                }

                // Set velocityCalculator field
                if (velocityCalculator != null)
                {
                    var velocityField = npcAnimationType.GetField("velocityCalculator", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    velocityField?.SetValue(npcAnimation, velocityCalculator);
                    ModLogger.Debug($"✓ Set SmoothedVelocityCalculator reference for {npc.name}");
                }

                // Set movement field
                if (npcMovement != null)
                {
                    var movementField = npcAnimationType.GetField("movement", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    movementField?.SetValue(npcAnimation, npcMovement);
                    ModLogger.Debug($"✓ Set NPCMovement reference for {npc.name}");
                }

                // Set npc field
                var npcField = npcAnimationType.GetField("npc", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                npcField?.SetValue(npcAnimation, npcComponent);

                ModLogger.Info($"✓ NPCAnimation fully configured for {npc.name} with all component references");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error configuring NPCAnimation references: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize animation system using the approach from TestController that works
        /// </summary>
        private static void InitializeAnimationSystemForNPC(GameObject npc, 
#if !MONO
            Il2CppScheduleOne.AvatarFramework.Avatar avatar
#else
            ScheduleOne.AvatarFramework.Avatar avatar
#endif
        )
        {
            try
            {
                ModLogger.Info($"Initializing animation system for {npc.name} using TestController approach...");
                
                // Look for AvatarAnimation component (TestController approach)
#if !MONO
                var avatarAnimationComponent = npc.GetComponentInChildren<Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation>();
                if (avatarAnimationComponent == null)
                {
                    // Try to find it by type name since IL2CPP can be tricky
                    var allComponents = npc.GetComponentsInChildren<Component>();
                    foreach (var comp in allComponents)
                    {
                        if (comp != null && comp.GetType().Name.Contains("AvatarAnimation"))
                        {
                            avatarAnimationComponent = comp as Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation;
                            break;
                        }
                    }
                }
#else
                var avatarAnimationComponent = npc.GetComponentInChildren<ScheduleOne.AvatarFramework.Animation.AvatarAnimation>();
#endif
                
                if (avatarAnimationComponent != null)
                {
                    avatarAnimationComponent.enabled = true;
                    ModLogger.Info($"✓ Found and enabled AvatarAnimation component: {avatarAnimationComponent.GetType().Name}");
                }
                else
                {
                    ModLogger.Warn($"AvatarAnimation component not found on {npc.name} - animations may not work properly");
                }
                
                // Look for SmoothedVelocityCalculator (TestController approach)
#if !MONO
                var velocityCalculatorComponent = npc.GetComponentInChildren<Il2CppScheduleOne.Tools.SmoothedVelocityCalculator>();
                if (velocityCalculatorComponent == null)
                {
                    var allComponents = npc.GetComponentsInChildren<Component>();
                    foreach (var comp in allComponents)
                    {
                        if (comp != null && comp.GetType().Name.Contains("SmoothedVelocityCalculator"))
                        {
                            velocityCalculatorComponent = comp as Il2CppScheduleOne.Tools.SmoothedVelocityCalculator;
                            break;
                        }
                    }
                }
#else
                var velocityCalculatorComponent = npc.GetComponentInChildren<ScheduleOne.Tools.SmoothedVelocityCalculator>();
#endif
                
                if (velocityCalculatorComponent != null)
                {
                    velocityCalculatorComponent.enabled = true;
                    // Apply TestController's working animation settings
                    velocityCalculatorComponent.SampleLength = 0.1f;
                    velocityCalculatorComponent.MaxReasonableVelocity = 15f;
                    ModLogger.Info($"✓ Found and configured SmoothedVelocityCalculator component");
                }
                else
                {
                    ModLogger.Warn($"SmoothedVelocityCalculator component not found on {npc.name}");
                }
                
                // Enable Animator component if present
                var animator = npc.GetComponentInChildren<UnityEngine.Animator>();
                if (animator != null)
                {
                    animator.enabled = true;
                    ModLogger.Info($"✓ Enabled Animator component for {npc.name}");
                }
                
                ModLogger.Info($"✓ Animation system initialized for {npc.name} using TestController approach");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error initializing animation system for {npc.name}: {e.Message}");
            }
        }

        /// <summary>
        /// Adds voice over and audio system for NPCs to support guard voice commands
        /// </summary>
        private static void AddAudioSystem(GameObject npc, object npcComponent, NPCType npcType)
        {
            try
            {
                // Add AudioSourceController for managing audio playback
#if !MONO
                var audioSourceController = npc.AddComponent<Il2CppScheduleOne.Audio.AudioSourceController>();
#else
                var audioSourceController = npc.AddComponent<ScheduleOne.Audio.AudioSourceController>();
#endif

                if (audioSourceController != null)
                {
                    // Configure audio settings
                    audioSourceController.DefaultVolume = 0.8f;
                    audioSourceController.RandomizePitch = true;
                    audioSourceController.MinPitch = 0.9f;
                    audioSourceController.MaxPitch = 1.1f;

                    // Set audio type based on NPC type
                    if (npcType == NPCType.JailGuard)
                    {
#if !MONO
                        audioSourceController.AudioType = Il2CppScheduleOne.Audio.EAudioType.FX;
#else
                        audioSourceController.AudioType = ScheduleOne.Audio.EAudioType.FX;
#endif
                    }
                    else
                    {
#if !MONO
                        audioSourceController.AudioType = Il2CppScheduleOne.Audio.EAudioType.FX;
#else
                        audioSourceController.AudioType = ScheduleOne.Audio.EAudioType.FX;
#endif
                    }

                    ModLogger.Debug($"✓ AudioSourceController added to {npc.name}");
                }

                // Add VOEmitter for voice over playback on the head bone (like police do)
#if !MONO
                var npc_casted = npcComponent as Il2CppScheduleOne.NPCs.NPC;
#else
                var npc_casted = npcComponent as ScheduleOne.NPCs.NPC;
#endif

                if (npc_casted != null)
                {
                    // We'll add the VOEmitter later when the avatar head bone is available
                    // For now, just mark that this NPC should have voice support
                    ModLogger.Debug($"✓ NPC {npc.name} marked for voice support - VOEmitter will be added when avatar is ready");
                }

                // For guards, add our custom JailNPCAudioController
                if (npcType == NPCType.JailGuard)
                {
                    var jailAudioController = npc.AddComponent<JailNPCAudioController>();
                    ModLogger.Debug($"✓ JailNPCAudioController added to guard {npc.name}");
                }

                ModLogger.Debug($"✓ Audio system configured for {npcType} NPC: {npc.name}");
            }
            catch (Exception e)
            {
                ModLogger.Error($"Error adding audio system to {npc.name}: {e.Message}");
            }
        }

    }
}