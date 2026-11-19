using System;

namespace GI_Subtitles.Services.Automation
{
    /// <summary>
    /// Tells the auto-start controller that a gate may have opened, once the work in hand is done. The dialogue area is
    /// saved from inside the Start button's own gates (the Region gate places the frame): told right there, the
    /// controller started translation while that start was still running, and the button then started it again.
    /// </summary>
    public sealed class GateRecheck
    {
        private readonly Action _recheck;
        private readonly Action<Action> _post;
        private bool _queued;

        /// <param name="recheck">The controller's <see cref="AutoStartController.OnGatesChanged"/>.</param>
        /// <param name="post">Runs an action after the current one (the UI dispatcher's BeginInvoke).</param>
        public GateRecheck(Action recheck, Action<Action> post)
        {
            ArgumentNullException.ThrowIfNull(recheck);
            ArgumentNullException.ThrowIfNull(post);
            _recheck = recheck;
            _post = post;
        }

        /// <summary>Asks for one recheck; several requests before it runs ask once.</summary>
        public void Request()
        {
            if (_queued) return;
            _queued = true;
            _post(() =>
            {
                _queued = false;
                _recheck();
            });
        }
    }
}
