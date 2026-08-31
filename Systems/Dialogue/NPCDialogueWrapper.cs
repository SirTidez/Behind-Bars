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
        /// <param name="npcGameObject">Root NPC object used for handler lookup.</param>
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
        /// <param name="choiceLabel">Case-insensitive choice label to observe.</param>
        /// <param name="callback">Callback to retain and invoke for each matching event.</param>
        /// <returns>This wrapper for fluent registration.</returns>
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
        /// <param name="nodeLabel">Case-insensitive node label to observe.</param>
        /// <param name="callback">Callback to retain and invoke for each matching event.</param>
        /// <returns>This wrapper for fluent registration.</returns>
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
        /// <remarks>
        /// This clears callback dictionaries only. Native event listeners remain attached
        /// until Dispose, so the wrapper can be reused without rebuilding its hooks.
        /// </remarks>
        public void ClearCallbacks()
        {
            _choiceCallbacks.Clear();
            _nodeCallbacks.Clear();
        }

        /// <summary>
        /// Removes UnityEvent listeners owned by this wrapper. Clearing callback dictionaries
        /// alone does not detach the wrapper from a native DialogueHandler.
        /// </summary>
        public void Dispose()
        {
            if (_eventHandler != null && _eventsHooked)
            {
                EventHelper.RemoveListener<string>(Internal_OnChoice, _eventHandler.onDialogueChoiceChosen);
                EventHelper.RemoveListener<string>(Internal_OnNode, _eventHandler.onDialogueNodeDisplayed);
            }

            if (_eventHandler != null)
            {
                foreach (var clearOnce in _oneShotConversationStartListeners)
                {
                    EventHelper.RemoveListener(clearOnce, _eventHandler.onConversationStart);
                }
            }

            _oneShotConversationStartListeners.Clear();
            ClearCallbacks();
            _eventHandler = null;
            _eventsHooked = false;
        }

        /// <summary>
        /// Starts a dialogue by container name present on the NPC's handler.
        /// </summary>
        /// <param name="containerName">Name of the registered container to start.</param>
        /// <param name="enableBehaviour">Whether native dialogue behavior should be enabled.</param>
        /// <param name="entryNodeLabel">Entry node label passed to the native handler.</param>
        public void Start(string containerName, bool enableBehaviour = true, string entryNodeLabel = "ENTRY")
        {
            if (string.IsNullOrEmpty(containerName))
                return;
            EnsureHandler();
            Handler?.StartDialogue(containerName, enableBehaviour, entryNodeLabel);
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
        /// <param name="text">Text to display.</param>
        /// <param name="durationSeconds">Native worldspace display duration in seconds.</param>
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
        /// <param name="key">Native reaction key; null/empty hides current worldspace text.</param>
        /// <param name="durationSeconds">Duration override, or -1 for native selection.</param>
        /// <param name="network">Whether to use the native networked reaction path.</param>
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
        /// <param name="text">Replacement text supplied to the native handler.</param>
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
            // Capture the exact handler used for subscription. Handler is a component
            // lookup and could later resolve a different child; removing from the stored
            // instance is what keeps Dispose balanced with AddListener.
            _eventHandler = Handler;
            // The current guard is set before the two AddListener calls and assumes the
            // EventHelper/native event surface succeeds; an exception leaves the wrapper
            // marked hooked until the caller disposes/recreates it.
            _eventsHooked = true;
            // Handler events are invoked from DialogueHandler.ChoiceCallback and DialogueCallback
            // These are UnityEvent<string>, so we use AddListener<string>
            EventHelper.AddListener<string>(Internal_OnChoice, _eventHandler.onDialogueChoiceChosen);
            EventHelper.AddListener<string>(Internal_OnNode, _eventHandler.onDialogueNodeDisplayed);
        }

        /// <summary>
        /// Builds a DialogueContainer with choice-based flow and registers it by name.
        /// Use this to define custom conversations for this NPC entirely from code.
        /// </summary>
        /// <param name="containerName">Name used for replacement/lookup in the handler.</param>
        /// <param name="configure">Builder callback that defines nodes, choices, and links.</param>
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
                // Replace an existing same-name container in place; otherwise append it.
                // This prevents repeated setup calls from creating ambiguous duplicates.
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
        /// <param name="containerName">Registered container name to use as an override.</param>
        /// <returns>True when the container and DialogueController were found and updated.</returns>
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
        /// <param name="containerName">Registered container name to use once.</param>
        /// <returns>True when the container and DialogueController were found and updated.</returns>
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

            // Clear the override as soon as the conversation actually starts. Keep a stable
            // listener reference so a scene transition can remove it if dialogue never opens.
            // The list owns every pending listener so Dispose can detach all of them.
            Action clearOnce = null;
            clearOnce = () =>
            {
                try { controller.ClearOverrideContainer(); } catch { }
                try { EventHelper.RemoveListener(clearOnce, _eventHandler?.onConversationStart); } catch { }
                _oneShotConversationStartListeners.Remove(clearOnce);
            };
            _eventHandler ??= Handler;
            try
            {
                EventHelper.AddListener(clearOnce, _eventHandler?.onConversationStart);
                _oneShotConversationStartListeners.Add(clearOnce);
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Immediately navigates this NPC's dialogue to a specific container and entry node.
        /// Returns true on success.
        /// </summary>
        /// <param name="containerName">Registered container name.</param>
        /// <param name="entryNodeLabel">Node label to enter.</param>
        /// <param name="enableBehaviour">Whether native dialogue behavior should be enabled.</param>
        /// <returns>True when the container was found and the native handler started it.</returns>
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
            Handler.StartDialogue(container, enableBehaviour, entryNodeLabel);
            return true;
        }

        private void Internal_OnChoice(string choiceLabel)
        {
            if (string.IsNullOrEmpty(choiceLabel))
                return;
            // Callback lists are retained registrations, not one-shot listeners. Copying
            // is intentionally avoided in the current implementation; callers should not
            // mutate registration state from inside a callback unless they accept list
            // iteration semantics.
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
            // Node callbacks follow the same case-insensitive retained-list semantics as
            // choices. Callback exceptions are contained so one subscriber cannot block
            // the remaining callbacks or native event flow.
            if (_nodeCallbacks.TryGetValue(nodeLabel, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    try { list[i]?.Invoke(); } catch { }
                }
            }
        }

#if MONO
        // Mono exposes the handler collection as a private field; IL2CPP exposes the
        // corresponding collection directly as a property (see Build/Use/Jump methods).
        private static FieldInfo dialogueContainersField = typeof(DialogueHandler).GetField("dialogueContainers", BindingFlags.NonPublic | BindingFlags.Instance);
#else
        // In IL2CPP, dialogueContainers is a property, not a field
#endif
        // Labels are case-insensitive and each list remains attached until ClearCallbacks
        // or Dispose; registration APIs intentionally support multiple callbacks/label.
        private readonly Dictionary<string, List<Action>> _choiceCallbacks = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action>> _nodeCallbacks = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        // Own pending one-shot override-clear delegates so they can be removed reliably.
        private readonly List<Action> _oneShotConversationStartListeners = new List<Action>();
        // Stored subscription target and guard used to balance event hookup/teardown.
        private DialogueHandler _eventHandler;
        private bool _eventsHooked;
    }
}

