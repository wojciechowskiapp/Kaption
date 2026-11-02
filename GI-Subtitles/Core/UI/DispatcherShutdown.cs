using System;

namespace GI_Subtitles.Core.UI
{
    /// <summary>
    /// Recognises the cancellation WPF raises for a Dispatcher.Invoke/InvokeAsync it aborted
    /// while shutting down ("A task was canceled."). Expected when the app closes mid-load.
    /// </summary>
    public static class DispatcherShutdown
    {
        /// <summary>True when <paramref name="ex"/> is a cancellation and the dispatcher has begun
        /// shutting down; a cancellation while it is still running is a real failure.</summary>
        public static bool IsShutdownCancellation(Exception ex, bool hasShutdownStarted)
            => hasShutdownStarted && ex is OperationCanceledException;
    }
}
