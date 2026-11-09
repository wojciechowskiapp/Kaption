using System;
using System.IO;

namespace GI_Subtitles.Services
{
    /// <summary>
    /// Writes matched translation entries to a daily dialogue log file.
    /// </summary>
    public static class DialogueLog
    {
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kaption", "logs");

        private static readonly object _lock = new object();
        private static string _currentFile;
        private static string _lastEntry;

        static DialogueLog()
        {
            Directory.CreateDirectory(_logDir);
            _currentFile = Path.Combine(_logDir, $"dialogue_{DateTime.Now:yyyy-MM-dd}.txt");
        }

        /// <summary>
        /// Log a matched translation entry to today's file.
        /// </summary>
        public static void Log(string header, string polishText)
        {
            if (string.IsNullOrWhiteSpace(polishText)) return;

            // Deduplicate consecutive identical entries
            string entry = $"{header}: {polishText}".Trim().TrimStart(':').Trim();
            if (entry == _lastEntry) return;
            _lastEntry = entry;

            lock (_lock)
            {
                try
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    string line = string.IsNullOrEmpty(header)
                        ? $"[{timestamp}] {polishText}"
                        : $"[{timestamp}] {header}: {polishText}";

                    File.AppendAllText(_currentFile, line + Environment.NewLine);
                }
                catch { /* Don't crash the app for logging failures */ }
            }
        }

        public static string GetCurrentLogPath() => _currentFile;
    }
}
