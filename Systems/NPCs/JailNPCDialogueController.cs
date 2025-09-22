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
        private JailNPCAudioController audioController;
        private float lastGreetingTime;
        private string currentState = "";

        protected virtual void Start()
        {
            jailNPC = GetComponent<BaseJailNPC>();
            baseController = GetComponent<DialogueController>();
            dialogueHandler = GetComponent<DialogueHandler>();
            audioController = GetComponent<JailNPCAudioController>();

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
                },
                new StateDialogue
                {
                    stateName = "Processing",
                    greeting = "Processing paperwork.",
                    interactions = new[] { "Give me a moment.", "Almost done.", "Working on it." },
                    playVO = true,
                    voType = EVOLineType.Greeting
                },

                // IntakeOfficer escort states
                new StateDialogue
                {
                    stateName = "EscortToHolding",
                    greeting = "Follow me to holding.",
                    interactions = new[] { "This way to holding.", "Stay close.", "Keep moving." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "EscortToMugshot",
                    greeting = "Follow me to the photo station.",
                    interactions = new[] { "Time for your mugshot.", "This way to photos.", "Keep moving." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "EscortToScanner",
                    greeting = "Follow me to the scanner.",
                    interactions = new[] { "Time for fingerprints.", "This way to the scanner.", "Move along." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "EscortToStorage",
                    greeting = "Follow me to storage.",
                    interactions = new[] { "Time to change clothes.", "This way to storage.", "Keep moving." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "EscortToCell",
                    greeting = "Follow me to your cell.",
                    interactions = new[] { "This way to your cell.", "Stay close.", "Almost there." },
                    playVO = true,
                    voType = EVOLineType.Command
                },

                // IntakeOfficer station states
                new StateDialogue
                {
                    stateName = "AtHolding",
                    greeting = "Wait here.",
                    interactions = new[] { "Stand by.", "Wait for instructions.", "Stay put." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "AtMugshot",
                    greeting = "Step up to the camera.",
                    interactions = new[] { "Look at the camera.", "Hold still.", "Don't move." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "AtScanner",
                    greeting = "Place your hand on the scanner.",
                    interactions = new[] { "Put your hand here.", "Press firmly.", "Hold still." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "AtStorage",
                    greeting = "Change into these clothes.",
                    interactions = new[] { "Put on the uniform.", "Get changed.", "Hurry up." },
                    playVO = true,
                    voType = EVOLineType.Command
                },
                new StateDialogue
                {
                    stateName = "AtCell",
                    greeting = "This is your cell.",
                    interactions = new[] { "Get inside.", "This is where you'll stay.", "In you go." },
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
            ModLogger.Debug($"UpdateGreetingForState called with state: '{state}' on {gameObject.name}");

            if (baseController == null)
            {
                ModLogger.Debug($"UpdateGreetingForState: baseController is null on {gameObject.name}");
                return;
            }

            currentState = state;
            ModLogger.Debug($"UpdateGreetingForState: Set currentState to '{currentState}' on {gameObject.name}");

            try
            {
                // Reset all greeting overrides
                if (baseController.GreetingOverrides == null)
                {
                    ModLogger.Debug($"UpdateGreetingForState: GreetingOverrides is null on {gameObject.name}");
                    return;
                }

                ModLogger.Debug($"UpdateGreetingForState: Resetting {baseController.GreetingOverrides.Count} greeting overrides on {gameObject.name}");
                foreach (var greetingOverride in baseController.GreetingOverrides)
                {
                    greetingOverride.ShouldShow = false;
                }

                // Find and activate the appropriate greeting for the current state
                var stateDialogue = stateDialogues.Find(sd => sd.stateName.Equals(state, System.StringComparison.OrdinalIgnoreCase));
                if (stateDialogue != null)
                {
                    var index = stateDialogues.IndexOf(stateDialogue);
                    ModLogger.Debug($"UpdateGreetingForState: Found state dialogue '{stateDialogue.stateName}' at index {index} for state '{state}' on {gameObject.name}");

                    if (index >= 0 && index < baseController.GreetingOverrides.Count)
                    {
                        baseController.GreetingOverrides[index].ShouldShow = true;
                        ModLogger.Debug($"Updated greeting for {gameObject.name} to state: {state}");
                    }
                    else
                    {
                        ModLogger.Debug($"UpdateGreetingForState: Index {index} is out of range (0-{baseController.GreetingOverrides.Count-1}) on {gameObject.name}");
                    }
                }
                else
                {
                    ModLogger.Debug($"No state dialogue found for state: {state} on {gameObject.name}. Available states: {string.Join(",", stateDialogues.ConvertAll(sd => sd.stateName))}");
                }
            }
            catch (System.Exception ex)
            {
                ModLogger.Error($"Error updating greeting for state {state} on {gameObject.name}: {ex.Message}\nStackTrace: {ex.StackTrace}");
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

            ModLogger.Debug($"SendContextualMessage: currentState='{currentState}', available states: {string.Join(",", stateDialogues.ConvertAll(sd => sd.stateName))}");

            var stateDialogue = stateDialogues.Find(sd => sd.stateName.Equals(currentState, System.StringComparison.OrdinalIgnoreCase));
            if (stateDialogue != null && stateDialogue.interactions.Length > 0)
            {
                var randomInteraction = stateDialogue.interactions[UnityEngine.Random.Range(0, stateDialogue.interactions.Length)];

                ModLogger.Debug($"Using state dialogue for '{currentState}': {randomInteraction}");

                if (jailNPC != null)
                {
                    jailNPC.TrySendNPCMessage(randomInteraction, 3f);
                    lastGreetingTime = Time.time;
                }

                // Play voice command if this is a guard and audio controller is available
                if (audioController != null && stateDialogue.playVO)
                {
                    PlayVoiceForState(stateDialogue.stateName, stateDialogue.voType);
                }
            }
            else
            {
                ModLogger.Debug($"No matching state dialogue found for '{currentState}', using default: {defaultGreeting}");

                // Fallback to default greeting
                if (jailNPC != null)
                {
                    jailNPC.TrySendNPCMessage(defaultGreeting, 3f);
                    lastGreetingTime = Time.time;
                }

                // Play default greeting voice
                if (audioController != null)
                {
                    PlayVoiceForState("Greeting", EVOLineType.Greeting);
                }
            }
        }

        /// <summary>
        /// Play voice command based on state and voice type
        /// </summary>
        /// <param name="stateName">Current NPC state</param>
        /// <param name="voiceType">Type of voice line to play</param>
        public virtual void PlayVoiceForState(string stateName, EVOLineType voiceType)
        {
            if (audioController == null || !audioController.IsReady())
            {
                return;
            }

            try
            {
                // Convert state and voice type to guard command
                var commandType = ConvertStateToGuardCommand(stateName, voiceType);

                // Determine if this should use radio effect (guards usually do)
                bool useRadio = gameObject.name.Contains("Guard") || gameObject.name.Contains("JailGuard");

                audioController.PlayGuardCommand(commandType, useRadio);

                ModLogger.Debug($"Playing voice command {commandType} for state {stateName} on {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error playing voice for state {stateName}: {e.Message}");
            }
        }

        /// <summary>
        /// Convert NPC state and voice type to guard command type
        /// </summary>
        /// <param name="stateName">Current NPC state</param>
        /// <param name="voiceType">Voice line type</param>
        /// <returns>Appropriate guard command type</returns>
        private JailNPCAudioController.GuardCommandType ConvertStateToGuardCommand(string stateName, EVOLineType voiceType)
        {
            // Convert based on state name first
            switch (stateName.ToLower())
            {
                case "escorting":
                    return JailNPCAudioController.GuardCommandType.Follow;

                case "working":
                case "cellcheck":
                    return JailNPCAudioController.GuardCommandType.CellCheck;

                case "alert":
                case "alerted":
                    return JailNPCAudioController.GuardCommandType.Alert;

                case "greeting":
                case "idle":
                    return JailNPCAudioController.GuardCommandType.Greeting;

                default:
                    // Fall back to voice type
                    switch (voiceType)
                    {
                        case EVOLineType.Command:
                            return JailNPCAudioController.GuardCommandType.Stop;

                        case EVOLineType.Alerted:
                            return JailNPCAudioController.GuardCommandType.Alert;

                        case EVOLineType.Angry:
                            return JailNPCAudioController.GuardCommandType.Warning;

                        case EVOLineType.Greeting:
                            return JailNPCAudioController.GuardCommandType.Greeting;

                        case EVOLineType.Acknowledge:
                            return JailNPCAudioController.GuardCommandType.AllClear;

                        default:
                            return JailNPCAudioController.GuardCommandType.Greeting;
                    }
            }
        }

        /// <summary>
        /// Send a specific guard command with voice
        /// </summary>
        /// <param name="commandType">Type of command to issue</param>
        /// <param name="message">Optional text message to display</param>
        /// <param name="useRadio">Whether to use radio effect</param>
        public virtual void SendGuardCommand(JailNPCAudioController.GuardCommandType commandType, string message = null, bool useRadio = true)
        {
            try
            {
                // Play voice command
                if (audioController != null && audioController.IsReady())
                {
                    audioController.PlayGuardCommand(commandType, useRadio);
                }

                // Send text message if provided
                if (!string.IsNullOrEmpty(message) && jailNPC != null)
                {
                    jailNPC.TrySendNPCMessage(message, 3f);
                }

                lastGreetingTime = Time.time;
                ModLogger.Debug($"Guard command {commandType} sent by {gameObject.name}");
            }
            catch (System.Exception e)
            {
                ModLogger.Error($"Error sending guard command {commandType}: {e.Message}");
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