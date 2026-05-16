using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace GI_Subtitles.Core.Logging
{
    public static class LogBuffer
    {
        private const int MaxEntries = 500;

        public static ObservableCollection<LogEntry> Entries { get; } = new ObservableCollection<LogEntry>();

        public static event Action EntryAdded;

        public static void Add(DateTime timestamp, string level, string message)
        {
            var entry = new LogEntry(timestamp, level, message);

            if (Application.Current?.Dispatcher != null &&
                !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => AddEntry(entry)));
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
            if (Application.Current?.Dispatcher != null &&
                !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => Entries.Clear()));
            }
            else
            {
                Entries.Clear();
            }
        }
    }
}
