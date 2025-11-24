using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Behind_Bars.Utils;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Dialogue;
#else
using ScheduleOne.Dialogue;
#endif

namespace Behind_Bars.Systems.Dialogue
{
    /// <summary>
    /// Dialogue wrapper for NPCs that provides helpers to create interactive conversations with branching dialogue trees,
    /// choice-based interactions, and dynamic responses. Adapted from S1API NPCDialogue to work with GameObject-based NPCs.
    /// </summary>
    /// <remarks>
    /// Use BuildAndRegisterContainer to define custom conversations, and UseContainerOnInteract to force a container when player interacts.
    /// Subscribe to choice and node events for dynamic dialogue behavior.
    /// </remarks>
    public sealed class NPCDialogueWrapper
    {
        /// <summary>
        /// Reference to the GameObject this dialogue wrapper is attached to.
        /// </summary>
        public readonly GameObject gameObject;

        /// <summary>
        /// Creates a new NPCDialogueWrapper for the given GameObject.
        /// </summary>
        public NPCDialogueWrapper(GameObject npcGameObject)
        {
            if (npcGameObject == null)
                throw new ArgumentNullException(nameof(npcGameObject));
            gameObject = npcGameObject;
        }

        /// <summary>
        /// Whether a dialogue is currently in progress for this NPC.
        /// </summary>
        public bool IsDialogueInProgress => Handler != null && Handler.IsDialogueInProgress;

        /// <summary>
        /// Register a callback to run when a choice with the given label is selected.
        /// Label must match the DialogueChoiceData.ChoiceLabel in your container.
        /// </summary>
        public NPCDialogueWrapper OnChoiceSelected(string choiceLabel, Action callback)
        {
            if (string.IsNullOrEmpty(choiceLabel) || callback == null)
                return this;
            EnsureHandler();
            EnsureEventHooks();
            if (!_choiceCallbacks.TryGetValue(choiceLabel, out var list))
            {
                list = new List<Action>();
                _choiceCallbacks[choiceLabel] = list;
            }
            list.Add(callback);
            return this;
        }

        /// <summary>
        /// Register a callback to run when a dialogue node with the given label is displayed.
        /// </summary>
        public NPCDialogueWrapper OnNodeDisplayed(string nodeLabel, Action callback)
        {
            if (string.IsNullOrEmpty(nodeLabel) || callback == null)
                return this;
            EnsureHandler();
            EnsureEventHooks();
            if (!_nodeCallbacks.TryGetValue(nodeLabel, out var list))
            {
                list = new List<Action>();
                _nodeCallbacks[nodeLabel] = list;
            }
            list.Add(callback);
            return this;
        }

        /// <summary>
        /// Removes all registered dialogue callbacks for this NPC.
        /// </summary>
        public void ClearCallbacks()
        {
            _choiceCallbacks.Clear();
            _nodeCallbacks.Clear();
        }

        /// <summary>
        /// Starts a dialogue by container name present on the NPC's handler.
        /// </summary>
        public void Start(string containerName, bool enableBehaviour = true, string entryNodeLabel = "ENTRY")
        {
            if (string.IsNullOrEmpty(containerName))
                return;
            EnsureHandler();
            Handler?.InitializeDialogue(containerName, enableBehaviour, entryNodeLabel);
        }

        /// <summary>
        /// Ends any active dialogue.
        /// </summary>
        public void End()
        {
            Handler?.EndDialogue();
        }

        /// <summary>
        /// Shows worldspace dialogue text at the NPC for a duration.
        /// </summary>
        public void ShowWorldText(string text, float durationSeconds)
        {
            if (string.IsNullOrEmpty(text))
                return;
            EnsureHandler();
            Handler?.ShowWorldspaceDialogue(text, durationSeconds);
        }

        /// <summary>
        /// Plays a reaction by key. If duration is -1 the underlying system decides duration.
        /// </summary>
        public void PlayReaction(string key, float durationSeconds = -1f, bool network = false)
        {
            if (string.IsNullOrEmpty(key))
            {
                Handler?.HideWorldspaceDialogue();
                return;
            }
            EnsureHandler();
            Handler?.PlayReaction(key, durationSeconds, network);
        }

        /// <summary>
        /// Overrides the shown dialogue text (e.g., for temporary notifications).
        /// You generally won't want to use this
        /// </summary>
        public void OverrideText(string text)
        {
            EnsureHandler();
            Handler?.OverrideShownDialogue(text);
        }

        /// <summary>
        /// Stops any active override and resumes normal dialogue display.
        /// </summary>
        public void StopOverride()
        {
            Handler?.StopOverride();
            var controller = Handler?.GetComponent<DialogueController>();
            controller?.ClearOverrideContainer();
        }

        /// <summary>
        /// Returns the DialogueHandler instance, if present.
        /// Uses GetComponentInChildren to match S1API behavior - DialogueHandler might be on a child object (like Avatar).
        /// </summary>
        public DialogueHandler Handler => gameObject.GetComponentInChildren<DialogueHandler>(true);

        /// <summary>
        /// Ensures there is a DialogueHandler component attached.
        /// Checks children first (matching S1API), then adds to root if not found.
        /// </summary>
        public void EnsureHandler()
        {
            if (Handler == null)
            {
                // Try to find on any child first (like Avatar)
                var childHandler = gameObject.GetComponentInChildren<DialogueHandler>(true);
                if (childHandler == null)
                {
                    // Not found on children, add to root
                    gameObject.AddComponent<DialogueHandler>();
                }
            }
        }

        private void EnsureEventHooks()
        {
            if (Handler == null || _eventsHooked)
                return;
            _eventsHooked = true;
            // Handler events are invoked from DialogueHandler.ChoiceCallback and DialogueCallback
            // These are UnityEvent<string>, so we use AddListener<string>
            EventHelper.AddListener<string>(Internal_OnChoice, Handler.onDialogueChoiceChosen);
            EventHelper.AddListener<string>(Internal_OnNode, Handler.onDialogueNodeDisplayed);
        }

        /// <summary>
        /// Builds a DialogueContainer with choice-based flow and registers it by name.
        /// Use this to define custom conversations for this NPC entirely from code.
        /// </summary>
        public void BuildAndRegisterContainer(string containerName, Action<DialogueContainerBuilder> configure)
        {
            if (string.IsNullOrEmpty(containerName) || configure == null)
                return;
            EnsureHandler();
            if (Handler == null)
                return;

            var contBuilder = new DialogueContainerBuilder();
            configure(contBuilder);
            var container = contBuilder.Build(containerName);

#if MONO
            var list = dialogueContainersField?.GetValue(Handler) as List<DialogueContainer>;
#else
            var list = Handler.dialogueContainers;
#endif
            if (list != null)
            {
                int idx = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    if (item != null && item.name == containerName)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                    list[idx] = container;
                else
                    list.Add(container);
            }
        }

        /// <summary>
        /// When the player interacts with this NPC, force using the named container for the next dialogue.
        /// Returns true if the container was found and applied.
        /// </summary>
        public bool UseContainerOnInteract(string containerName)
        {
            if (string.IsNullOrEmpty(containerName))
                return false;
            EnsureHandler();
            if (Handler == null)
                return false;

#if MONO
            var list = dialogueContainersField?.GetValue(Handler) as List<DialogueContainer>;
#else
            var list = Handler.dialogueContainers;
#endif
            if (list == null)
                return false;
            DialogueContainer container = null;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item != null && item.name == containerName)
                {
                    container = item;
                    break;
                }
            }
            if (container == null)
                return false;

            var controller = Handler.GetComponent<DialogueController>();
            if (controller == null)
                return false;

            controller.SetOverrideContainer(container);
            ModLogger.Debug($"NPCDialogueWrapper: UseContainerOnInteract called - container '{containerName}' set as override");
            return true;
        }

        /// <summary>
        /// When the player interacts with this NPC, force using the named container once for the next dialogue.
        /// After the conversation begins, the override is automatically cleared so subsequent interactions use normal flow.
        /// Returns true if the container was found and applied.
        /// </summary>
        public bool UseContainerOnInteractOnce(string containerName)
        {
            if (string.IsNullOrEmpty(containerName))
                return false;
            EnsureHandler();
            if (Handler == null)
                return false;

#if MONO
            var list = dialogueContainersField?.GetValue(Handler) as List<DialogueContainer>;
#else
            var list = Handler.dialogueContainers;
#endif
            if (list == null)
                return false;
            DialogueContainer container = null;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item != null && item.name == containerName)
                {
                    container = item;
                    break;
                }
            }
            if (container == null)
                return false;

            var controller = Handler.GetComponent<DialogueController>();
            if (controller == null)
                return false;

            controller.SetOverrideContainer(container);

            // Clear the override as soon as the conversation actually starts
            void ClearOnce()
            {
                try { controller.ClearOverrideContainer(); } catch { }
                try { EventHelper.RemoveListener((System.Action)ClearOnce, Handler.onConversationStart); } catch { }
            }
            try { EventHelper.AddListener((System.Action)ClearOnce, Handler.onConversationStart); } catch { }

            return true;
        }

        /// <summary>
        /// Immediately navigates this NPC's dialogue to a specific container and entry node.
        /// Returns true on success.
        /// </summary>
        public bool JumpTo(string containerName, string entryNodeLabel, bool enableBehaviour = false)
        {
            if (string.IsNullOrEmpty(containerName) || string.IsNullOrEmpty(entryNodeLabel))
                return false;
            EnsureHandler();
            if (Handler == null)
                return false;
#if MONO
            var list = dialogueContainersField?.GetValue(Handler) as List<DialogueContainer>;
#else
            var list = Handler.dialogueContainers;
#endif
            if (list == null)
                return false;
            DialogueContainer container = null;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item != null && item.name == containerName)
                {
                    container = item;
                    break;
                }
            }
            if (container == null)
                return false;
            Handler.InitializeDialogue(container, enableBehaviour, entryNodeLabel);
            return true;
        }

        private void Internal_OnChoice(string choiceLabel)
        {
            if (string.IsNullOrEmpty(choiceLabel))
                return;
            if (_choiceCallbacks.TryGetValue(choiceLabel, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try { list[i]?.Invoke(); } catch { }
                }
            }
        }

        private void Internal_OnNode(string nodeLabel)
        {
            if (string.IsNullOrEmpty(nodeLabel))
                return;
            if (_nodeCallbacks.TryGetValue(nodeLabel, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try { list[i]?.Invoke(); } catch { }
                }
            }
        }

#if MONO
        private static FieldInfo dialogueContainersField = typeof(DialogueHandler).GetField("dialogueContainers", BindingFlags.NonPublic | BindingFlags.Instance);
#else
        // In IL2CPP, dialogueContainers is a property, not a field
#endif
        private readonly Dictionary<string, List<Action>> _choiceCallbacks = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action>> _nodeCallbacks = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        private bool _eventsHooked;
    }
}

