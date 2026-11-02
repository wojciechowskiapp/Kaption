using System;
using System.Collections.Generic;

namespace GI_Subtitles.Services.Automation
{
    /// <summary>First closed start gate, in the order the start path checks them; None = start would pass.</summary>
    public enum StartGate { None = 0, Session, InitialSync, Engine, Matcher, Region, GameRunning, DisplayModeTip, OverlayOverlap }

    public enum AutoStartState { Disabled, WaitingForGame, Blocked, Translating, PausedByUser }

    public interface IAutoStartHost
    {
        bool IsTranslationRunning { get; }

        /// <summary>Silent check of all eight gates, no UI.</summary>
        StartGate FirstClosedGate();

        /// <summary>Runs the normal gated start path; returns whether translation is running afterwards.</summary>
        bool StartThroughGates();

        void StopTranslation();
    }

    /// <summary>
    /// Auto-start: runs the normal gated start when the game is in the foreground and a silent check finds every gate
    /// open, and stops only a session it started when the game exits. It never bypasses a gate or opens a gate dialog.
    /// Not thread-safe by design: call it on the UI thread only.
    /// </summary>
    public sealed class AutoStartController
    {
        /// <summary>After a failed or externally stopped start, no retry until this has passed and a new trigger arrives.</summary>
        public static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(5);

        private readonly IAutoStartHost _host;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Queue<Action> _deferred = new();
        private bool _enabled;
        private bool _paused;
        private bool _inHostCall;
        private DateTimeOffset? _failedAt;

        public AutoStartController(IAutoStartHost host, bool enabled, Func<DateTimeOffset> clock = null)
        {
            ArgumentNullException.ThrowIfNull(host);
            _host = host;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _enabled = enabled;
            State = !enabled ? AutoStartState.Disabled
                : host.IsTranslationRunning ? AutoStartState.Translating
                : AutoStartState.WaitingForGame;
        }

        public AutoStartState State { get; private set; }

        /// <summary>The first closed gate while <see cref="State"/> is Blocked (None if the host cannot say); otherwise None.</summary>
        public StartGate BlockingGate { get; private set; }

        /// <summary>True while a session this controller started is running; only that session is stopped on game exit.</summary>
        public bool StartedByController { get; private set; }

        public bool IsGameForeground { get; private set; }

        public bool IsGameRunning { get; private set; }

        /// <summary>Raised once after a call that changed <see cref="State"/> or <see cref="BlockingGate"/>.</summary>
        public event EventHandler StateChanged;

        /// <summary>On: re-evaluates at once. Off: never stops translation, only gives up a session it started.</summary>
        public void SetEnabled(bool enabled) => Run(() => ApplyEnabled(enabled), deferIfReentrant: true);

        /// <summary>Foreground watcher edge. Repeats are ignored; losing the foreground never stops translation.</summary>
        public void OnGameForeground(bool isForeground) => Run(() => ApplyForeground(isForeground), deferIfReentrant: true);

        /// <summary>Process watcher edge. An exit stops the session only if this controller started it.</summary>
        public void OnGameRunningChanged(bool isRunning) => Run(() => ApplyGameRunning(isRunning), deferIfReentrant: true);

        /// <summary>Engine, matcher, sync, region or session changed.</summary>
        public void OnGatesChanged() => Run(Evaluate, deferIfReentrant: false);

        /// <summary>A manual start (button or hotkey) succeeded: ends the pause. A manual session is never auto-stopped.</summary>
        public void OnUserStarted() => Run(ApplyUserStarted, deferIfReentrant: false);

        /// <summary>The user stopped translation: no automatic start until <see cref="OnUserStarted"/>.</summary>
        public void OnUserStopped() => Run(ApplyUserStopped, deferIfReentrant: false);

        /// <summary>The app stopped translation itself (e.g. region cleared). Treated like a failed start.</summary>
        public void OnTranslationStoppedExternally() => Run(ApplyStoppedExternally, deferIfReentrant: false);

        private void Run(Action step, bool deferIfReentrant)
        {
            if (_inHostCall)
            {
                // Called back from inside a host call. Watcher and setting edges wait until this call is done; gate and
                // start/stop notices describe the call itself (an echo, not the user) and the host is re-read after it.
                if (deferIfReentrant) _deferred.Enqueue(step);
                return;
            }

            AutoStartState stateBefore = State;
            StartGate gateBefore = BlockingGate;
            step();
            while (_deferred.Count > 0) _deferred.Dequeue()();
            if (State != stateBefore || BlockingGate != gateBefore) StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyEnabled(bool enabled)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (!enabled) StartedByController = false;
            Evaluate();
        }

        private void ApplyForeground(bool isForeground)
        {
            if (IsGameForeground == isForeground) return;
            IsGameForeground = isForeground;
            Evaluate();
        }

        private void ApplyGameRunning(bool isRunning)
        {
            if (isRunning)
            {
                if (IsGameRunning) return;
                IsGameRunning = true;
                Evaluate();   // the GameRunning gate may just have opened
                return;
            }

            // Not edge-filtered: an exit must stop our session even if the launch was never reported.
            IsGameRunning = false;
            IsGameForeground = false;
            if (StartedByController && _host.IsTranslationRunning)
            {
                StartedByController = false;
                CallHost(_host.StopTranslation);
            }
            Evaluate();
        }

        private void ApplyUserStarted()
        {
            _paused = false;
            StartedByController = false;
            SetState(_enabled ? AutoStartState.Translating : AutoStartState.Disabled);
        }

        private void ApplyUserStopped()
        {
            _paused = true;
            StartedByController = false;
            SetState(_enabled ? AutoStartState.PausedByUser : AutoStartState.Disabled);
        }

        private void ApplyStoppedExternally()
        {
            StartedByController = false;
            _failedAt = _clock();
            Evaluate();
        }

        // Derives State and starts when nothing holds it back. Only trigger edges get as far as the start: every other
        // caller has just disabled, left the foreground or armed the cooldown.
        private void Evaluate()
        {
            if (!_enabled) { SetState(AutoStartState.Disabled); return; }
            if (_host.IsTranslationRunning) { SetState(AutoStartState.Translating); return; }

            StartedByController = false;
            if (_paused) { SetState(AutoStartState.PausedByUser); return; }
            if (!IsGameForeground) { SetState(AutoStartState.WaitingForGame); return; }

            StartGate gate = CallHost(_host.FirstClosedGate);
            if (gate != StartGate.None || InRetryCooldown())
            {
                SetState(AutoStartState.Blocked, gate);
                return;
            }

            // Stamped when the start returns (or throws), so a dialog left open does not use up the cooldown.
            bool started = false;
            try { started = CallHost(_host.StartThroughGates); }
            finally { if (!started) _failedAt = _clock(); }

            StartedByController = started;
            if (started) SetState(AutoStartState.Translating);
            else SetState(AutoStartState.Blocked, CallHost(_host.FirstClosedGate));
        }

        private bool InRetryCooldown()
        {
            if (_failedAt is not DateTimeOffset failedAt) return false;
            DateTimeOffset now = _clock();
            if (now < failedAt)
            {
                _failedAt = now;   // the clock stepped back: wait one cooldown from here, not the size of the step
                return true;
            }
            if (now - failedAt < RetryCooldown) return true;
            _failedAt = null;
            return false;
        }

        private void SetState(AutoStartState state, StartGate gate = StartGate.None)
        {
            State = state;
            BlockingGate = state == AutoStartState.Blocked ? gate : StartGate.None;
        }

        private T CallHost<T>(Func<T> call)
        {
            _inHostCall = true;
            try { return call(); }
            finally { _inHostCall = false; }
        }

        private void CallHost(Action call)
        {
            _inHostCall = true;
            try { call(); }
            finally { _inHostCall = false; }
        }
    }
}
