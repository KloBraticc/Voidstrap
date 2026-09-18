using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wpf.Ui.Animations
{
    public static class RenderReady
    {
        private const int WarmUpFrames = 6;

        private const int WarmUpTimeoutMilliseconds = 2500;

        private const double MaximumHoldMilliseconds = 12000d;

        private const int HoldPollMilliseconds = 120;

        private static readonly bool Portable = OperatingSystem.IsLinux();

        private static readonly Dictionary<Dispatcher, State> States = new();

        public static bool IsPortable => Portable;

        private static long _layoutMotionUntil;

        public static bool IsLayoutMotionActive => Environment.TickCount64 < Interlocked.Read(ref _layoutMotionUntil);

        public static void HoldLayoutMotion(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
                return;

            double milliseconds = Math.Min(duration.TotalMilliseconds, MaximumHoldMilliseconds);
            long until = Environment.TickCount64 + (long)milliseconds;
            while (true)
            {
                long current = Interlocked.Read(ref _layoutMotionUntil);
                if (until <= current)
                    return;
                if (Interlocked.CompareExchange(ref _layoutMotionUntil, until, current) == current)
                    return;
            }
        }

        public static void Run(DispatcherObject? owner, Action action)
        {
            if (action is null)
                return;

            if (!Portable || !CanDefer(owner))
            {
                action();
                return;
            }

            GetState(owner!.Dispatcher).Run(action);
        }

        public static void Warm(DispatcherObject? owner)
        {
            if (!Portable || !CanDefer(owner))
                return;

            GetState(owner!.Dispatcher).Warm();
        }

        public static void Hold(DispatcherObject? owner, TimeSpan duration)
        {
            if (!Portable || !CanDefer(owner) || duration <= TimeSpan.Zero)
                return;

            GetState(owner!.Dispatcher).Hold(duration);
        }

        private static bool CanDefer(DispatcherObject? owner)
        {
            return owner is not null &&
                   owner.Dispatcher is not null &&
                   owner.Dispatcher.CheckAccess() &&
                   !owner.Dispatcher.HasShutdownStarted &&
                   !owner.Dispatcher.HasShutdownFinished;
        }

        private static State GetState(Dispatcher dispatcher)
        {
            lock (States)
            {
                if (!States.TryGetValue(dispatcher, out State? state))
                {
                    state = new State();
                    States[dispatcher] = state;
                }

                return state;
            }
        }

        private sealed class State
        {
            private readonly List<Action> _pending = new();

            private DispatcherTimer? _deadline;

            private DispatcherTimer? _holdPoll;

            private bool _watching;

            private bool _ready;

            private bool _holding;

            private int _frames;

            private long _holdUntil;

            internal void Warm()
            {
                if (_ready)
                    return;

                StartWatch();
            }

            internal void Run(Action action)
            {
                if (_ready)
                {
                    Invoke(action);
                    return;
                }

                _pending.Add(action);
                StartWatch();
            }

            internal void Hold(TimeSpan duration)
            {
                double milliseconds = Math.Min(duration.TotalMilliseconds, MaximumHoldMilliseconds);
                long until = Environment.TickCount64 + (long)milliseconds;
                if (until > _holdUntil)
                    _holdUntil = until;

                if (_holding)
                    return;

                _holding = true;
                CompositionTarget.Rendering += OnHoldRendering;
                _holdPoll = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(HoldPollMilliseconds)
                };
                _holdPoll.Tick += OnHoldTick;
                _holdPoll.Start();
            }

            private void StartWatch()
            {
                if (_watching)
                    return;

                _watching = true;
                _frames = 0;
                CompositionTarget.Rendering += OnWarmUpRendering;
                _deadline = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(WarmUpTimeoutMilliseconds)
                };
                _deadline.Tick += OnDeadline;
                _deadline.Start();
            }

            private void OnWarmUpRendering(object? sender, EventArgs e)
            {
                _frames++;
                if (_frames >= WarmUpFrames)
                    Release();
            }

            private void OnDeadline(object? sender, EventArgs e)
            {
                Release();
            }

            private void Release()
            {
                if (!_watching)
                    return;

                _watching = false;
                _ready = true;
                CompositionTarget.Rendering -= OnWarmUpRendering;
                if (_deadline is not null)
                {
                    _deadline.Stop();
                    _deadline.Tick -= OnDeadline;
                    _deadline = null;
                }

                if (_pending.Count == 0)
                    return;

                Action[] queued = _pending.ToArray();
                _pending.Clear();
                foreach (Action action in queued)
                    Invoke(action);
            }

            private void OnHoldRendering(object? sender, EventArgs e)
            {
            }

            private void OnHoldTick(object? sender, EventArgs e)
            {
                if (Environment.TickCount64 < _holdUntil)
                    return;

                ReleaseHold();
            }

            private void ReleaseHold()
            {
                if (!_holding)
                    return;

                _holding = false;
                CompositionTarget.Rendering -= OnHoldRendering;
                if (_holdPoll is null)
                    return;

                _holdPoll.Stop();
                _holdPoll.Tick -= OnHoldTick;
                _holdPoll = null;
            }

            private static void Invoke(Action action)
            {
                try
                {
                    action();
                }
                catch (InvalidOperationException)
                {
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }
}
