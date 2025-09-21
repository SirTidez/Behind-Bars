using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.VoiceOver;
#else
using ScheduleOne.Dialogue;
using ScheduleOne.NPCs;
using ScheduleOne.VoiceOver;
#endif

namespace Behind_Bars.Systems.NPCs
{
    /// <summary>
    /// Custom dialogue controller for jail NPCs that provides context-aware dialog based on NPC state
    /// </summary>
    public class JailNPCDialogueController : MonoBehaviour
    {
        [System.Serializable]
        public class StateDialogue
        {
            public string stateName;
            public string greeting;
            public string[] interactions;
            public bool playVO = true;
            public EVOLineType voType = EVOLineType.Greeting;
        }

        [Header("NPC Dialog Configuration")]
        public List<StateDialogue> stateDialogues = new List<StateDialogue>();
        public string defaultGreeting = "Hello.";
        public float greetingCooldown = 5f;

        private BaseJailNPC jailNPC;
        private DialogueController baseController;
        private DialogueHandler dialogueHandler;
        private float lastGreetingTime;
        private string currentState = "";

        protected virtual void Start()
        {
            jailNPC = GetComponent<BaseJailNPC>();
            baseController = GetComponent<DialogueController>();
            dialogueHandler = GetComponent<DialogueHandler>();

            if (jailNPC == null)
            {
                ModLogger.Error($"JailNPCDialogueController on {gameObject.name} requires BaseJailNPC component");
                return;
            }

            // Initialize default state dialogues if none are configured
            if (stateDialogues.Count == 0)
            {
                InitializeDefaultStateDialogues();
            }

            // Set up greeting overrides
            SetupGreetingOverrides();

            ModLogger.Debug($"JailNPCDialogueController initialized for {gameObject.name}");
        }

        protected virtual void InitializeDefaultStateDialogues()
        {
            stateDialogues.AddRange(new StateDialogue[]
            {
                new StateDialogue
                {
                    stateName = "Idle",
                    greeting = "Hello.",
                    interactions = new[] { "Hi there.", "Good day.", "What do you need?" },
                    playVO = true,
                    voType = EVOLineType.Greeting
                },
                new StateDialogue
                {
                    stateName = "Working",
                    greeting = "I'm busy right now.",
                    interactions = new[] { "Can't talk, I'm working.", "Come back later." },
                    playVO = true,
                    voType = EVOLineType.Greeting
                },
                new StateDialogue
                {
                    stateName = "Escorting",
                    greeting = "Follow me.",
                    interactions = new[] { "Stay close.", "Keep moving.", "This way." },
                    playVO = true,
                    voType = EVOLineType.Command
                }
            });
        }

        protected virtual void SetupGreetingOverrides()
        {
            if (baseController == null) return;

            try
            {
                // Clear existing greeting overrides
                baseController.GreetingOverrides.Clear();

                // Add our custom greeting overrides
                foreach (var stateDialogue in stateDialogues)
                {
                    var greetingOverride = new DialogueController.GreetingOverride
                    {
                        Greeting = stateDialogue.greeting,
                        ShouldShow = false, // Will be controlled by UpdateGreetingForState
                        PlayVO = stateDialogue.playVO,
                        VOType = stateDialogue.voType
                    };

                    baseController.AddGreetingOverride(greetingOverride);
                }

                ModLogger.Debug($"Set up {stateDialogues.Count} greeting overrides for {gameObject.name}");
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error setting up greeting overrides for {gameObject.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Update the NPC's greeting based on their current state
        /// </summary>
        /// <param name="state">The current state of the NPC</param>
        public virtual void UpdateGreetingForState(string state)
        {
            if (baseController == null) return;

            currentState = state;

            try
            {
                // Reset all greeting overrides
                foreach (var greetingOverride in baseController.GreetingOverrides)
                {
                    greetingOverride.ShouldShow = false;
                }

                // Find and activate the appropriate greeting for the current state
                var stateDialogue = stateDialogues.Find(sd => sd.stateName.Equals(state, System.StringComparison.OrdinalIgnoreCase));
                if (stateDialogue != null)
                {
                    var index = stateDialogues.IndexOf(stateDialogue);
                    if (index >= 0 && index < baseController.GreetingOverrides.Count)
                    {
                        baseController.GreetingOverrides[index].ShouldShow = true;
                        ModLogger.Debug($"Updated greeting for {gameObject.name} to state: {state}");
                    }
                }
                else
                {
                    ModLogger.Debug($"No state dialogue found for state: {state} on {gameObject.name}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating greeting for state {state} on {gameObject.name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a contextual message based on current state
        /// </summary>
        /// <param name="messageType">Type of message (greeting, interaction, instruction)</param>
        public virtual void SendContextualMessage(string messageType = "interaction")
        {
            if (Time.time - lastGreetingTime < greetingCooldown)
            {
                return;
            }

            var stateDialogue = stateDialogues.Find(sd => sd.stateName.Equals(currentState, System.StringComparison.OrdinalIgnoreCase));
            if (stateDialogue != null && stateDialogue.interactions.Length > 0)
            {
                var randomInteraction = stateDialogue.interactions[UnityEngine.Random.Range(0, stateDialogue.interactions.Length)];

                if (jailNPC != null)
                {
                    jailNPC.TrySendNPCMessage(randomInteraction, 3f);
                    lastGreetingTime = Time.time;
                }
            }
            else
            {
                // Fallback to default greeting
                if (jailNPC != null)
                {
                    jailNPC.TrySendNPCMessage(defaultGreeting, 3f);
                    lastGreetingTime = Time.time;
                }
            }
        }

        /// <summary>
        /// Add a custom state dialogue configuration
        /// </summary>
        /// <param name="stateName">Name of the state</param>
        /// <param name="greeting">Greeting message for this state</param>
        /// <param name="interactions">Array of possible interaction messages</param>
        /// <param name="playVO">Whether to play voice over</param>
        /// <param name="voType">Type of voice over to play</param>
        public virtual void AddStateDialogue(string stateName, string greeting, string[] interactions, bool playVO = true, EVOLineType voType = EVOLineType.Greeting)
        {
            var stateDialogue = new StateDialogue
            {
                stateName = stateName,
                greeting = greeting,
                interactions = interactions,
                playVO = playVO,
                voType = voType
            };

            stateDialogues.Add(stateDialogue);

            // Update greeting overrides if base controller is available
            if (baseController != null)
            {
                var greetingOverride = new DialogueController.GreetingOverride
                {
                    Greeting = greeting,
                    ShouldShow = false,
                    PlayVO = playVO,
                    VOType = voType
                };

                baseController.AddGreetingOverride(greetingOverride);
            }

            ModLogger.Debug($"Added state dialogue for {stateName} on {gameObject.name}");
        }

        /// <summary>
        /// Get the current dialogue state
        /// </summary>
        /// <returns>Current state name</returns>
        public string GetCurrentDialogueState()
        {
            return currentState;
        }
    }
}