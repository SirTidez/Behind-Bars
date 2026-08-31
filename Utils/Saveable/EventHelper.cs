using System;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Behind_Bars.Utils.Saveable
{
    /// <summary>
    /// Cross-compatible wrappers for Unity events used by the saveable patches.
    /// The class keeps the runtime-specific wrapper delegate needed to remove a
    /// subscription on both Mono and IL2CPP.
    /// </summary>
    /// <remarks>
    /// Tracking is process-wide and keyed by the caller delegate, not by an event
    /// instance. A delegate can therefore be tracked only once per map, and a
    /// removal must use the same event that received it. The helper has no general
    /// EventTrigger removal API; trigger entries remain in the component's
    /// <c>triggers</c> collection unless the owning code removes them separately.
    /// </remarks>
    public static class EventHelper
    {
        /// <summary>
        /// INTERNAL: Map from caller actions to the delegate passed to Unity for
        /// non-generic event subscriptions.
        /// </summary>
        /// <remarks>
        /// This map stores no event reference and is not automatically cleared on
        /// object destruction. Pair every add/remove during the owning lifecycle
        /// to avoid duplicate suppression and retained delegate references.
        /// </remarks>
        internal static readonly Dictionary<Action, Delegate> SubscribedActions = new Dictionary<Action, Delegate>();

        /// <summary>
        /// INTERNAL: Adds a listener through a caller-supplied subscription method.
        /// </summary>
        /// <param name="listener">The action / method you want to subscribe.</param>
        /// <param name="subscribe">The subscribe method to call.</param>
        /// <remarks>
        /// This low-level overload assumes both delegates are non-null; null input
        /// is not normalized here. It records the original action because the
        /// supplied subscription method is responsible for any wrapper conversion.
        /// </remarks>
        internal static void AddListener(Action listener, Action<Action> subscribe)
        {
            if (SubscribedActions.ContainsKey(listener))
                return;
            subscribe(listener);
            SubscribedActions.Add(listener, listener);
        }

        /// <summary>
        /// INTERNAL: Removes a listener through a caller-supplied unsubscribe method.
        /// </summary>
        /// <param name="listener">The action / method you want to unsubscribe.</param>
        /// <param name="unsubscribe">The unsubscribe method to call.</param>
        /// <remarks>
        /// Unknown actions are ignored. The caller-supplied method receives the
        /// original action, and the bookkeeping entry is removed afterward; this
        /// overload has no event identity or destruction cleanup of its own.
        /// </remarks>
        internal static void RemoveListener(Action listener, Action<Action> unsubscribe)
        {
            if (!SubscribedActions.TryGetValue(listener, out _))
                return;
            unsubscribe(listener);
            SubscribedActions.Remove(listener);
        }

        /// <summary>
        /// INTERNAL: Map from generic caller delegates to runtime-specific Unity
        /// wrapper delegates used for safe removal on IL2CPP and Mono.
        /// </summary>
        /// <remarks>
        /// The map is delegate-only and process-wide. It does not identify the
        /// event instance, so the same generic action cannot be tracked twice and
        /// a wrong event on removal can leave an original listener attached but
        /// untracked.
        /// </remarks>
        private static readonly Dictionary<Delegate, Delegate> SubscribedGenericActions = new Dictionary<Delegate, Delegate>();

        /// <summary>
        /// Adds a listener to a non-generic Unity event.
        /// </summary>
        /// <param name="listener">The action / method you want to subscribe.</param>
        /// <param name="unityEvent">The event you want to subscribe to.</param>
        /// <remarks>
        /// Unlike the sibling helper in <c>Behind_Bars.Utils</c>, this overload
        /// assumes <paramref name="unityEvent"/> is non-null and does not guard a
        /// null listener before consulting the dictionary. Null input therefore
        /// follows the underlying dictionary/Unity exception behavior. Duplicate
        /// delegate values are suppressed by the static map.
        /// </remarks>
        public static void AddListener(Action listener, UnityEvent unityEvent)
        {
            if (SubscribedActions.ContainsKey(listener))
                return;

#if !MONO
            // On IL2CPP prefer System.Action to avoid UnityAction .ctor issues
            System.Action wrapped = new System.Action(listener);
            unityEvent.AddListener(wrapped);
#else
            UnityAction wrapped = new UnityAction(listener);
            unityEvent.AddListener(wrapped);
#endif
            SubscribedActions.Add(listener, wrapped);
        }

        /// <summary>
        /// Adds an EventTrigger entry in a cross-compatible manner.
        /// Use this from Mono mods so IL2CPP handles the actual Entry construction.
        /// </summary>
        /// <param name="trigger">Target EventTrigger component.</param>
        /// <param name="eventType">The EventTriggerType to subscribe to.</param>
        /// <param name="listener">Callback invoked when the event fires.</param>
        /// <remarks>
        /// The parameterless callback is wrapped in a generated
        /// <see cref="Action{BaseEventData}"/> lambda. That wrapper, rather than
        /// the original action, is what is tracked by the trigger overload, so
        /// callers cannot later remove it by passing the original action to the
        /// ordinary <see cref="RemoveListener(Action, UnityEvent)"/> overload.
        /// </remarks>
        public static void AddEventTrigger(EventTrigger trigger, EventTriggerType eventType, Action listener)
        {
            if (trigger == null || listener == null)
                return;

            AddEventTrigger(trigger, eventType, (_)=> listener());
        }

        /// <summary>
        /// Adds an EventTrigger entry with access to BaseEventData.
        /// </summary>
        /// <param name="trigger">Target EventTrigger component.</param>
        /// <param name="eventType">The EventTriggerType to subscribe to.</param>
        /// <param name="listener">Callback invoked with BaseEventData when the event fires.</param>
        /// <remarks>
        /// A new <see cref="EventTrigger.Entry"/> is appended on every call; no
        /// duplicate detection is performed. The entry remains in
        /// <paramref name="trigger"/>. Null trigger/listener input is ignored.
        /// </remarks>
        public static void AddEventTrigger(EventTrigger trigger, EventTriggerType eventType, Action<BaseEventData> listener)
        {
            if (trigger == null || listener == null)
                return;

            var entry = new EventTrigger.Entry { eventID = eventType };
            AddListener(listener, entry.callback);
            trigger.triggers.Add(entry);
        }

        /// <summary>
        /// Removes a listener previously added to a non-generic Unity event.
        /// </summary>
        /// <param name="listener">The action / method you want to unsubscribe.</param>
        /// <param name="unityEvent">The event you want to unsubscribe from.</param>
        /// <remarks>
        /// The overload does not null-check its inputs. It clears the map entry
        /// before passing the stored wrapper to Unity; using a different event can
        /// therefore leave the original event subscribed but untracked.
        /// </remarks>
        public static void RemoveListener(Action listener, UnityEvent unityEvent)
        {
            SubscribedActions.TryGetValue(listener, out Delegate? wrappedAction);
            SubscribedActions.Remove(listener);
            if (wrappedAction == null)
                return;
#if !MONO
            if (wrappedAction is System.Action sys)
                unityEvent.RemoveListener(sys);
#else
            if (wrappedAction is UnityAction ua)
                unityEvent.RemoveListener(ua);
#endif
        }

        /// <summary>
        /// Adds a listener for a generic Unity event in an IL2CPP-safe manner.
        /// </summary>
        /// <typeparam name="T">Event payload type.</typeparam>
        /// <param name="listener">The action / method you want to subscribe.</param>
        /// <param name="unityEvent">The generic event you want to subscribe to.</param>
        /// <remarks>
        /// Null listener/event input is ignored. Duplicate delegate values are
        /// suppressed by a process-wide map that does not retain event identity.
        /// </remarks>
        public static void AddListener<T>(Action<T> listener, UnityEvent<T> unityEvent)
        {
            if (listener == null || unityEvent == null)
                return;

            if (SubscribedGenericActions.ContainsKey(listener))
                return;

#if !MONO
            // Use System.Action<T> wrapper for IL2CPP
            System.Action<T> wrapped = new System.Action<T>(listener);
            unityEvent.AddListener(wrapped);
#else
            UnityAction<T> wrapped = new UnityAction<T>(listener);
            unityEvent.AddListener(wrapped);
#endif
            SubscribedGenericActions.Add(listener, wrapped);
        }

        /// <summary>
        /// Removes a listener for a generic Unity event added via
        /// <see cref="AddListener{T}(Action{T}, UnityEvent{T})"/>.
        /// </summary>
        /// <typeparam name="T">Event payload type.</typeparam>
        /// <param name="listener">The action / method you want to unsubscribe.</param>
        /// <param name="unityEvent">The generic event you want to unsubscribe from.</param>
        /// <remarks>
        /// Null listener/event input and unknown delegates are ignored. The map is
        /// delegate-only, so a wrong event can leave the original subscription in
        /// place after its bookkeeping has been removed.
        /// </remarks>
        public static void RemoveListener<T>(Action<T> listener, UnityEvent<T> unityEvent)
        {
            if (listener == null || unityEvent == null)
                return;

            if (!SubscribedGenericActions.TryGetValue(listener, out Delegate wrapped))
                return;

#if !MONO
            if (wrapped is System.Action<T> sys)
                unityEvent.RemoveListener(sys);
#else
            if (wrapped is UnityAction<T> ua)
                unityEvent.RemoveListener(ua);
#endif
            SubscribedGenericActions.Remove(listener);
        }
    }
}

