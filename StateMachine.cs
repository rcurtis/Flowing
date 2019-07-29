using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace Flowing.Library
{
    public class StateMachine
    {
        private ILogger _log;
        private ILogger _spammyLog;
        private readonly List<State> _states = new List<State>();
        private readonly ConcurrentQueue<IMessage> _events = new ConcurrentQueue<IMessage>();
        private readonly Queue<State> _waitingTransitions = new Queue<State>();
        private readonly object _lock = new object();
        private readonly Thread _creationThread;
        private Stopwatch _stopwatch;

        private TimeSpan _elapsedSinceLastUpdate = TimeSpan.Zero;
        private List<State.RemindersCallback> _remindersToRemove = new List<State.RemindersCallback>();

        public State CurrentState { get; private set; }

        public StateMachine(ILogger logger, ILogger spammyLog)
        {
            _log = logger;
            _spammyLog = spammyLog;
            _creationThread = Thread.CurrentThread;
        }

        private void EnsureOriginThread()
        {
            if (Thread.CurrentThread != _creationThread)
                throw new ThreadStateException("Must be called from the thread that created the StateMachine");
        }

        public void Update()
        {
            if (_stopwatch == null)
            {
                _stopwatch = Stopwatch.StartNew();
            }
            else
            {
                _elapsedSinceLastUpdate = _stopwatch.Elapsed;
                _stopwatch.Restart();
            }

            EnsureOriginThread();
            UpdateReminderCallbacks();
            PumpEvents();
        }

        private void UpdateReminderCallbacks()
        {
            if (CurrentState == null)
                return;

            foreach (var reminder in CurrentState.RemindersCallbacks)
            {
                reminder.TimeSpentWaiting += _elapsedSinceLastUpdate.TotalSeconds;
                if (reminder.TimeSpentWaiting >= reminder.Timeout)
                {
                    PushMessage(reminder.Message);
                    _remindersToRemove.Add(reminder);
                }
            }
            // Remove all elements from reminders that were executed above.
            CurrentState.RemindersCallbacks = CurrentState.RemindersCallbacks
                .Except(_remindersToRemove).ToList();

            // Do the same for all the parent states.
            var parent = CurrentState.Parent;
            while (parent != null)
            {
                _remindersToRemove.Clear();
                foreach (var reminder in parent.RemindersCallbacks)
                {
                    reminder.TimeSpentWaiting += _elapsedSinceLastUpdate.TotalSeconds;
                    if (reminder.TimeSpentWaiting >= reminder.Timeout)
                    {
                        PushMessage(reminder.Message);
                        _remindersToRemove.Add(reminder);
                    }
                }

                parent.RemindersCallbacks = parent.RemindersCallbacks
                    .Except(_remindersToRemove).ToList();
                parent = parent.Parent;
            }
        }

        private void PerformTransitions()
        {
            if (_waitingTransitions.Count == 0)
                return;

            while (_waitingTransitions.Count > 0)
            {
                var destinationState = _waitingTransitions.Dequeue();

                if (destinationState == null)
                {
                    continue;
                }

                if (CurrentState == null)
                {
                    // This is our initial state.
                    CurrentState = destinationState;
                    CurrentState.Arrive();
                    return;
                }
                else if (destinationState.GetType() == CurrentState.GetType())
                {
                    _log.Debug("Ignoring state change request, we are already in the state: " +
                                      $"{CurrentState.GetType().Name}");
                    return;
                }

                _log.Info($"State transition: from={CurrentState.GetType().Name}; to={destinationState.GetType().Name}");

                var commonAncestor = FindLowestCommonAncestor(destinationState);

                if (commonAncestor == null)
                {
                    commonAncestor = destinationState;
                }

                NotifyAncestorsOfExitIfExist(commonAncestor);
                NotifyAncestorsOfEnterIfExist(destinationState, commonAncestor);

                CurrentState = destinationState;
                CurrentState.Arrive();
            }
        }

        private bool IsChildState(State destination)
        {
            var parent = destination?.Parent;
            while (parent != null)
            {
                if (parent == CurrentState)
                    return true;
                parent = parent.Parent;
            }
            return false;
        }

        private bool IsSuperState(State destination)
        {
            var parent = CurrentState?.Parent;
            while (parent != null)
            {
                if (parent == destination)
                    return true;
                parent = parent.Parent;
            }
            return false;
        }

        private State FindLowestCommonAncestor(State destination)
        {
            var ancestor = CurrentState;
            while (ancestor != null)
            {
                var destinationAncestor = destination.Parent;
                while (destinationAncestor != null)
                {
                    if (destinationAncestor == ancestor)
                        return ancestor;
                    destinationAncestor = destinationAncestor.Parent;
                }
                ancestor = ancestor.Parent;
            }
            return null;
        }

        /// <summary>
        /// Call Enter on all states from (but not including) the common ancestor
        /// to (and including) the destination state
        /// </summary>
        private void NotifyAncestorsOfEnterIfExist(State destinationState, State lowestAncestor)
        {
            if (lowestAncestor == null)
            {
                _log.Info("No ancestor found for exits.  Assuming vertical ancestry movement.");
                lowestAncestor = CurrentState;
            }

            var graph = new List<State> { destinationState };
            var ancestor = destinationState.Parent;
            while (ancestor != null && ancestor != lowestAncestor && ancestor != CurrentState)
            {
                graph.Add(ancestor);
                ancestor = ancestor.Parent;
            }

            // reverse the graph since you must call enter from the parent states first
            graph.Reverse();

            foreach (var state in graph)
            {
                state.Enter();
            }
        }

        /// <summary>
        /// Calls Exit on all states from the current state through all parent states to (but not including)
        /// the common ancestor state.
        /// </summary>
        private void NotifyAncestorsOfExitIfExist(State lowestAncestor)
        {
            var graph = new List<State>();

            var nextAncestor = CurrentState;
            while (nextAncestor != null && nextAncestor != lowestAncestor)
            {
                graph.Add(nextAncestor);
                nextAncestor = nextAncestor.Parent;
            }

            foreach (var state in graph)
            {
                state.Exit();
                state.ClearAllReminders();
            }
        }

        public void InitialState(State state)
        {
            if (!_states.Contains(state))
                throw new Exception($"Initial state '{state}' not found");

            _waitingTransitions.Enqueue(state);
        }

        public void InitialState<T>(T type)
        {
            InitialState(typeof(T));
        }

        public void InitialState(Type type)
        {
            var found = _states.FirstOrDefault(p => p.GetType() == type);
            if (found == null)
            {
                throw new Exception($"Initial state unknown: {type}");
            }

            _waitingTransitions.Enqueue(found);
        }

        public void AddState(State state)
        {
            lock (_lock)
            {
                if (_states.Contains(state))
                    throw new Exception("Duplicate states are not allowed");

                foreach (var st in _states)
                {
                    if (st.GetType() == state.GetType())
                        throw new Exception("Duplicate state types are not allowed");
                }

                state.TransitionEvent += HandleTransition;
                _states.Add(state);
            }
        }

        private void HandleTransition(Type newState)
        {
            lock (_lock)
            {
                var stateFound = _states.First(state => state.GetType() == newState);
                if (stateFound == null)
                    throw new Exception($"State transition requested to unknown state '{newState}'");

                _waitingTransitions.Enqueue(stateFound);
            }
        }

        /// <summary>
        /// Could be called from other threads, don't pump events from here.
        /// </summary>
        public void PushMessage(IMessage evnt)
        {
            lock (_lock)
            {
                if (_log.IsDebugEnabled || _spammyLog.IsDebugEnabled)
                {
                    if (IsSpammyMessage(evnt) && _spammyLog.IsDebugEnabled)
                        _spammyLog.Debug($"Queueing event: {evnt.GetType().Name}");
                    else
                        _log.Debug($"Queueing event: {evnt.GetType().Name}");
                }
                _events.Enqueue(evnt);
            }
        }

        private void PumpEvents()
        {
            // Transition events take precedence over regular state machine events.  We always handle
            // them before any business related events.
            PerformTransitions();
            if (_events.IsEmpty)
                return;

            IMessage evnt;
            bool success;
            lock (_lock)
            {
                success = _events.TryDequeue(out evnt);
            }
            if (!success)
                throw new Exception("Failed to process Event in state machine!");

            if (_log.IsDebugEnabled || _spammyLog.IsDebugEnabled)
            {
                var logMsg = $"Processing event: state={CurrentState.GetType().Name};";
                if (IsSpammyMessage(evnt))
                    _spammyLog.Debug(logMsg);
                else
                    _log.Debug(logMsg);
            }

            var handled = false;

            bool handledByIMessage = false;
            if (CurrentState.EventsSubscribed.ContainsKey(typeof(IMessage)))
            {
                var handler = CurrentState.EventsSubscribed[typeof(IMessage)];
                if (handler != null)
                    handledByIMessage = handler(evnt);

                if (handledByIMessage)
                    _log.Info($"Event {evnt.GetType().Name} handled by {CurrentState.GetType().Name} (IMessage)");

                handled = handledByIMessage;
            }

            // It is not possible for a state to be subscribed to both the generic 'IMessage'
            // and a concrete implementation of the generic interface.  This issue is handled
            // in State.cs when the state tries to subscribe to events.
            if (CurrentState.EventsSubscribed.ContainsKey(evnt.GetType()))
            {
                var handler = CurrentState.EventsSubscribed[evnt.GetType()];
                if (handler != null)
                    handled = handler(evnt);

                if (handled && !handledByIMessage)
                    _log.Info($"Event {evnt.GetType().Name} handled by {CurrentState.GetType().Name} (type-specific)");
            }

            if (handled)
                return;

            // Search up the parent graph
            var parent = CurrentState.Parent;
            while (parent != null)
            {
                if (parent.EventsSubscribed.ContainsKey(typeof(IMessage)))
                {
                    var handler = parent.EventsSubscribed[typeof(IMessage)];
                    handled = handler(evnt);
                    if (handled)
                    {
                        if (handled)
                            _log.Info($"Event {evnt.GetType().Name} handled by {parent.GetType().Name} (IMessage)");
                        return;
                    }
                }
                if (parent.EventsSubscribed.ContainsKey(evnt.GetType()))
                {
                    var handler = parent.EventsSubscribed[evnt.GetType()];
                    handled = handler?.Invoke(evnt) ?? false;
                    if (handled)
                        _log.Info($"Event {evnt.GetType().Name} handled by {parent.GetType().Name} (type-specific)");
                    return;
                }
                parent = parent.Parent;
            }

            if (_log.IsDebugEnabled || _spammyLog.IsDebugEnabled)
            {
                var logMsg = $"Event {evnt.GetType().Name} NOT HANDLED by state machine (currentState={CurrentState.GetType().Name})";
                if (IsSpammyMessage(evnt))
                    _spammyLog.Debug(logMsg);
                else
                    _log.Debug(logMsg);
            }
        }

        public void Shutdown()
        {
            foreach (var state in _states)
            {
                state.TransitionEvent -= HandleTransition;
            }
        }

        /// <summary>
        /// Decides whether a particular IMessage is too spammy for the regular state machine log file.
        /// </summary>
        private static bool IsSpammyMessage(IMessage msg)
        {
            if (msg == null)
                return false;

            switch (msg.GetType().Name)
            {
                case "ProgressiveBroadcast":
                    return true;

                default:
                    return false;
            }
        }

        public bool IsInStateOrSubState(Type t)
        {
            var found = _states.First(it => it.GetType() == t);
            return IsSuperState(found);
        }
    }
}
