using System;
using System.Collections.Generic;
using UnityEngine.Events;
using Behind_Bars.Helpers;

#if !MONO
using Il2CppScheduleOne.Dialogue;
#else
using ScheduleOne.Dialogue;
#endif

namespace Behind_Bars.Systems.Dialogue
{
    /// <summary>
    /// A utility class that listens for and responds to specific dialogue choices in the game's dialogue system.
    /// Standalone implementation based on S1API DialogueChoiceListener.
    /// </summary>
    public static class DialogueChoiceListener
    {
        private sealed class Registration
        {
            public readonly DialogueHandler Handler;
#if !MONO
            public readonly System.Action<string> ChoiceListener;
#else
            public readonly UnityAction<string> ChoiceListener;
#endif
            public string ExpectedChoiceLabel;
            public Action Callback;

            public Registration(DialogueHandler handler, string expectedChoiceLabel, Action callback)
            {
                Handler = handler;
                ExpectedChoiceLabel = expectedChoiceLabel;
                Callback = callback;
#if !MONO
                ChoiceListener = HandleChoiceSelected;
#else
                ChoiceListener = new UnityAction<string>(HandleChoiceSelected);
#endif
            }

            public void Attach()
            {
                Handler?.onDialogueChoiceChosen?.AddListener(ChoiceListener);
            }

            public void Detach()
            {
                Handler?.onDialogueChoiceChosen?.RemoveListener(ChoiceListener);
            }

            private void HandleChoiceSelected(string choice)
            {
                if (choice != ExpectedChoiceLabel)
                {
                    return;
                }

                var callback = Callback;
                DialogueChoiceListener.Unregister(Handler);

                try
                {
                    callback?.Invoke();
                }
                catch (Exception e)
                {
                    ModLogger.Error($"DialogueChoiceListener.OnChoice failed: {e.Message}\n{e.StackTrace}");
                }
            }
        }

        private static readonly Dictionary<DialogueHandler, Registration> _registrations = new Dictionary<DialogueHandler, Registration>();

        /// <summary>
        /// Registers a specific dialogue choice with a callback to be invoked when the choice is selected.
        /// </summary>
        /// <param name="handlerRef">The reference to the DialogueHandler that manages dialogue choices.</param>
        /// <param name="label">The label identifying the specific dialogue choice to be registered.</param>
        /// <param name="action">The callback action to execute when the dialogue choice is selected.</param>
        public static void Register(DialogueHandler handlerRef, string label, Action action)
        {
            if (handlerRef == null || string.IsNullOrEmpty(label) || action == null)
            {
                ModLogger.Warn("DialogueChoiceListener.Register called with null parameters");
                return;
            }

            try
            {
                Unregister(handlerRef);

                var registration = new Registration(handlerRef, label, action);
                _registrations[handlerRef] = registration;
                registration.Attach();
            }
            catch (Exception e)
            {
                ModLogger.Error($"DialogueChoiceListener.Register failed: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Removes the registered callback for a dialogue handler, if one exists.
        /// </summary>
        public static void Unregister(DialogueHandler handlerRef)
        {
            if (handlerRef == null)
            {
                return;
            }

            try
            {
                if (_registrations.TryGetValue(handlerRef, out var registration))
                {
                    registration.Detach();
                    _registrations.Remove(handlerRef);
                }
            }
            catch (Exception e)
            {
                ModLogger.Error($"DialogueChoiceListener.Unregister failed: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

