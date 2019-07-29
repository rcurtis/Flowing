using System;
using System.Collections.Generic;
using System.Text;

namespace Flowing.Library
{
    public abstract class State
    {
        public delegate bool MessageHandler(IMessage evnt);

        public delegate void TransitionEventHandler(Type newState);

        public TransitionEventHandler TransitionEvent { get; set; }

        internal Dictionary<Type, MessageHandler> EventsSubscribed = new Dictionary<Type, MessageHandler>();

        public State Parent { get; set; }

        internal List<RemindersCallback> RemindersCallbacks = new List<RemindersCallback>();

        /// <summary>
        /// Called whenever we enter this state OR a child state for the first time.
        /// </summary>
        public abstract void Enter();

        /// <summary>
        /// Called whenever we exit this state.
        /// </summary>
        public abstract void Exit();

        /// <summary>
        /// Called whenever we arrive at a specific state.  This does not get called for parents.
        /// </summary>
        public virtual void Arrive()
        {
        }

        public void Subscribe(Type type, MessageHandler handler)
        {
            if (EventsSubscribed.ContainsKey(type))
            {
                throw new StateMachineException($"Duplicate event subscription for type: {type}");
            }

            if (EventsSubscribed.ContainsKey(typeof(IMessage)))
            {
                throw new StateMachineException("You may not subscribe to the generic message (IMessage) and a concrete message.");
            }

            EventsSubscribed.Add(type, handler);
        }

        protected void Transition<T>()
        {
            Transition(typeof(T));
        }

        protected void Transition(Type state)
        {
            TransitionEvent?.Invoke(state);
        }

        /// <summary>
        /// Sets a 'reminder' in the state machine to dispatch the provided msg after a given amount of time.
        /// If the state that set the reminder is no longer the current state, no message will be sent.
        /// </summary>
        protected void RemindMeIn(double timeInSeconds, IMessage msg)
        {
            RemindersCallbacks.Add(new RemindersCallback { Timeout = timeInSeconds, Message = msg });
        }

        public void ClearAllReminders()
        {
            RemindersCallbacks.Clear();
        }

        public void ClearReminderOfType(Type msgType)
        {
            RemindersCallbacks.RemoveAll(it => it.Message.GetType() == msgType);
        }

        protected State()
        {
            Parent = null;
        }

        internal class RemindersCallback
        {
            public double Timeout { get; set; }
            public double TimeSpentWaiting { get; set; }
            public IMessage Message { get; set; }
        }
    }
}
