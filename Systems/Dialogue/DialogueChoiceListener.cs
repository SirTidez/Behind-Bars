using System;
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
        /// <summary>
        /// Stores the label of the expected dialogue choice that, when selected,
        /// triggers the associated callback action in the dialogue system.
        /// </summary>
        private static string _expectedChoiceLabel;

        /// <summary>
        /// Represents a delegate invoked when a specific dialogue choice is selected during interaction.
        /// </summary>
        private static Action _callback;

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

            _expectedChoiceLabel = label;
            _callback = action;

            try
            {
                void ForwardCall() => OnChoice();

#if !MONO
                // IL2CPP-safe: explicit method binding via wrapper
                handlerRef.onDialogueChoiceChosen.AddListener((UnityAction<string>)delegate (string choice)
                {
                    if (choice == _expectedChoiceLabel)
                        ((UnityAction)ForwardCall).Invoke();
                });
#else
                // Mono event subscription
                handlerRef.onDialogueChoiceChosen.AddListener((UnityAction<string>)delegate (string choice)
                {
                    if (choice == _expectedChoiceLabel)
                        ForwardCall();
                });
#endif
            }
            catch (Exception e)
            {
                ModLogger.Error($"DialogueChoiceListener.Register failed: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Executes the registered callback when the expected dialogue choice is selected.
        /// </summary>
        private static void OnChoice()
        {
            try
            {
                _callback?.Invoke();
                // Clear callback after use (one-time use)
                _callback = null;
                _expectedChoiceLabel = null;
            }
            catch (Exception e)
            {
                ModLogger.Error($"DialogueChoiceListener.OnChoice failed: {e.Message}\n{e.StackTrace}");
            }
        }
    }
}

