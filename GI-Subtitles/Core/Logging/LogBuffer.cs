using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace GI_Subtitles.Core.Logging
{
    /// <summary>
    /// In-memory history of recent log records.
    ///
    /// There are two views of the same data on purpose:
    ///
    ///  * <see cref="History"/> — a plain list guarded by a lock. This is the
    ///    source of truth, written synchronously by whichever thread logged,
    ///    and it is what diagnostic bundles read.
    ///  * <see cref="Entries"/> — an ObservableCollection for the Logs tab to
    ///    bind to. WPF requires collection changes on the UI thread, so this
    ///    one is updated through the dispatcher.
    ///
    /// Keeping them separate matters more than it looks. An earlier version
    /// had only the ObservableCollection and marshalled reads onto the
    /// dispatcher with Invoke — which blocks until the UI thread services the
    /// callback. That deadlocks whenever the UI thread is busy or wedged, i.e.
    /// exactly when someone is trying to collect diagnostics about a frozen
    /// app. Diagnostics must never depend on the thread being diagnosed.
    /// </summary>
    public static class LogBuffer
    {
        /// <summary>
        /// Holds roughly the last few hours of activity at event granularity.
        /// Sized for diagnostic bundles: the root log4net level is DEBUG (with
        /// the file appender thresholded at INFO), so this buffer is the only
        /// place debug-level history exists, and 500 entries would evict it
        /// long before a user got round to reporting a problem.
        ///
        /// Safe for the bound ListView because it is virtualised
        /// (SettingsWindow.xaml, LogListView). Per-frame OCR logging stays
        /// behind the `Debug` config flag; ungating it would flood this.
        /// </summary>
        private const int MaxEntries = 5000;

        private static readonly List<LogEntry> History = new List<LogEntry>(MaxEntries);
        private static readonly object HistoryGate = new object();

        /// <summary>UI-bindable projection. Only touch this on the UI thread.</summary>
        public static ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        public static event Action EntryAdded;

        /// <summary>
        /// Point-in-time copy of the history, safe to call from any thread and
        /// guaranteed not to block on the UI thread.
        /// </summary>
        public static LogEntry[] Snapshot()
        {
            lock (HistoryGate) return History.ToArray();
        }

        public static void Add(DateTime timestamp, string level, string message)
        {
            var entry = new LogEntry(timestamp, level, message);

            // Record first, synchronously, so the history is complete even if
            // the UI never gets round to processing the dispatcher callback.
            lock (HistoryGate)
            {
                History.Add(entry);
                if (History.Count > MaxEntries)
                {
                    History.RemoveRange(0, History.Count - MaxEntries);
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // BeginInvoke, never Invoke: logging must not block the caller
                // on the UI thread's availability.
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => AddEntry(entry)));
            }
            else
            {
                AddEntry(entry);
            }
        }

        private static void AddEntry(LogEntry entry)
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }
            EntryAdded?.Invoke();
        }

        public static void Clear()
        {
            lock (HistoryGate) History.Clear();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Entries.Clear()));
            }
            else
            {
                Entries.Clear();
            }
        }
    }
}
