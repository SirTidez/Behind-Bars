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
        // A registration owns the exact delegate instance added to UnityEvent. Keeping
        // that instance stable is required for RemoveListener to detach across Mono and
        // IL2CPP; recreating the wrapper would leave a stale callback behind.
        private sealed class Registration
        {
            /// <summary>Handler whose choice event is being observed.</summary>
            public readonly DialogueHandler Handler;
#if !MONO
            /// <summary>Stable IL2CPP delegate passed to the native UnityEvent.</summary>
            public readonly System.Action<string> ChoiceListener;
#else
            /// <summary>Stable Mono UnityAction passed to the UnityEvent.</summary>
            public readonly UnityAction<string> ChoiceListener;
#endif
            /// <summary>Exact choice label that qualifies for the callback.</summary>
            public string ExpectedChoiceLabel;
            /// <summary>One-shot callback owned by this registration.</summary>
            public Action Callback;

            /// <summary>Creates a registration and its runtime-specific stable delegate.</summary>
            /// <param name="handler">Dialogue handler to observe.</param>
            /// <param name="expectedChoiceLabel">Exact label to match.</param>
            /// <param name="callback">Action to invoke once after a match.</param>
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

            /// <summary>Attaches the owned delegate to the handler's choice event.</summary>
            public void Attach()
            {
                Handler?.onDialogueChoiceChosen?.AddListener(ChoiceListener);
            }

            /// <summary>Detaches the same owned delegate from the handler's choice event.</summary>
            public void Detach()
            {
                Handler?.onDialogueChoiceChosen?.RemoveListener(ChoiceListener);
            }

            // Matching is exact and one-shot. Unregister before invoking user code so a
            // callback that opens/rebuilds dialogue cannot observe the same choice again.
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

        // A handler has one active registration. Register replaces the previous one so
        // stale callbacks cannot accumulate when dialogue nodes are revisited.
        private static readonly Dictionary<DialogueHandler, Registration> _registrations = new Dictionary<DialogueHandler, Registration>();

        /// <summary>
        /// Registers a specific dialogue choice with a callback to be invoked when the choice is selected.
        /// </summary>
        /// <param name="handlerRef">The reference to the DialogueHandler that manages dialogue choices.</param>
        /// <param name="label">The label identifying the specific dialogue choice to be registered.</param>
        /// <param name="action">The callback action to execute when the dialogue choice is selected.</param>
        /// <remarks>
        /// Existing registration state for the handler is detached first. The new
        /// registration remains until the exact label is selected or Unregister is called.
        /// </remarks>
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
        /// <param name="handlerRef">Handler whose owned registration should be detached.</param>
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

