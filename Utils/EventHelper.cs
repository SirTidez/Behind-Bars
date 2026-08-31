using System;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Behind_Bars.Utils
{
    /// <summary>
    /// Cross-compatible event helper for subscribing/unsubscribing Unity events.
    /// Based on S1API EventHelper for IL2CPP/Mono compatibility.
    /// </summary>
    /// <remarks>
    /// The helper owns process-wide listener maps so it can retain the exact
    /// wrapper delegate required by <c>RemoveListener</c>. A map is keyed only by
    /// the caller's delegate, not by the Unity event, so one delegate can be
    /// tracked by this helper only once per value type. Callers must unsubscribe
    /// with the same delegate and event; passing a different event removes the
    /// map entry without removing the listener from the original event.
    /// </remarks>
    public static class EventHelper
    {
        /// <summary>
        /// Process-wide map from caller delegates to the runtime-specific Unity
        /// wrapper used for non-generic <see cref="UnityEvent"/> subscriptions.
        /// </summary>
        /// <remarks>
        /// The map intentionally stores no event reference. Entries remain live
        /// until a matching remove call succeeds, so lifecycle owners must pair
        /// subscriptions and removals to avoid retaining delegates.
        /// </remarks>
        private static readonly Dictionary<Action, Delegate> SubscribedActions = new Dictionary<Action, Delegate>();

        /// <summary>
        /// Adds a listener to a non-generic Unity event.
        /// </summary>
        /// <param name="listener">The action / method you want to subscribe.</param>
        /// <param name="unityEvent">The event you want to subscribe to.</param>
        /// <remarks>
        /// Null listeners/events are ignored. Duplicate delegate values are also
        /// ignored because the static map has no event dimension. Mono wraps the
        /// action in <see cref="UnityAction"/>; IL2CPP uses <see cref="System.Action"/>
        /// to avoid the UnityAction constructor path that is unsafe there.
        /// </remarks>
        public static void AddListener(Action listener, UnityEvent unityEvent)
        {
            if (listener == null || unityEvent == null)
                return;
            
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
        /// Removes a listener previously added to a non-generic Unity event.
        /// </summary>
        /// <param name="listener">The action / method you want to unsubscribe.</param>
        /// <param name="unityEvent">The event you want to unsubscribe from.</param>
        /// <remarks>
        /// Null inputs and unknown delegates are ignored. The tracking entry is
        /// removed before the wrapper is passed to Unity, so using the wrong event
        /// loses the helper's bookkeeping while leaving the original subscription
        /// in place.
        /// </remarks>
        public static void RemoveListener(Action listener, UnityEvent unityEvent)
        {
            if (listener == null || unityEvent == null)
                return;
            
            if (!SubscribedActions.TryGetValue(listener, out Delegate wrappedAction))
                return;
            
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
        /// Adds a listener for <see cref="UnityEvent{T0}"/> in an IL2CPP-safe manner.
        /// </summary>
        /// <typeparam name="T">Event payload type.</typeparam>
        /// <param name="listener">The action / method you want to subscribe.</param>
        /// <param name="unityEvent">The generic event you want to subscribe to.</param>
        /// <remarks>
        /// Null inputs and duplicate delegate values are ignored. The generic map
        /// is process-wide and keyed by delegate only; it does not retain the event
        /// instance and therefore requires the same event on removal.
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
        /// Removes a listener for <see cref="UnityEvent{T0}"/> added via
        /// <see cref="AddListener{T}(Action{T}, UnityEvent{T})"/>.
        /// </summary>
        /// <typeparam name="T">Event payload type.</typeparam>
        /// <param name="listener">The action / method you want to unsubscribe.</param>
        /// <param name="unityEvent">The generic event you want to unsubscribe from.</param>
        /// <remarks>
        /// Null inputs and unknown delegates are ignored. As with the non-generic
        /// overload, the map is delegate-only and is cleared after the remove path;
        /// a wrong event can leave the original listener attached but untracked.
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

        /// <summary>
        /// Process-wide map from generic caller delegates to their runtime-specific
        /// Unity wrapper delegates for safe removal on IL2CPP and Mono.
        /// </summary>
        /// <remarks>
        /// This map is keyed by delegate value only and does not identify the event
        /// to which a wrapper was added. It is cleared only by the matching generic
        /// remove path.
        /// </remarks>
        private static readonly Dictionary<Delegate, Delegate> SubscribedGenericActions = new Dictionary<Delegate, Delegate>();
    }
}

