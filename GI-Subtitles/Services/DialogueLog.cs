using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GI_Subtitles.Services.Rendering;

namespace GI_Subtitles.Services
{
    /// <summary>
    /// Writes matched translation entries to a daily dialogue log file
    /// and maintains an in-memory session transcript for the overlay card system.
    /// </summary>
    public static class DialogueLog
    {
        private static readonly string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kaption", "logs");

        private static readonly object _lock = new object();
        private static string _currentFile;
        private static string _lastEntry;

        // In-memory session transcript (newest first)
        private static readonly List<TranscriptEntry> _transcript = new List<TranscriptEntry>();
        private const int MaxTranscriptEntries = 200;

        /// <summary>
        /// A single entry in the dialogue transcript.
        /// </summary>
        public class TranscriptEntry
        {
            public DateTime Timestamp { get; set; }
            public string NpcName { get; set; }
            public string EnglishText { get; set; }
            public string TranslatedText { get; set; }
            public string QuestName { get; set; }
            public MatchSource MatchSource { get; set; }
        }

        static DialogueLog()
        {
            Directory.CreateDirectory(_logDir);
            _currentFile = Path.Combine(_logDir, $"dialogue_{DateTime.Now:yyyy-MM-dd}.txt");
        }

        /// <summary>
        /// Log a matched translation entry. Writes to file and adds to in-memory transcript.
        /// </summary>
        public static void Log(string header, string polishText, string englishOcr,
            string questName = null, MatchSource matchSource = MatchSource.None)
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
                    // Add to in-memory transcript
                    var transcriptEntry = new TranscriptEntry
                    {
                        Timestamp = DateTime.Now,
                        NpcName = header,
                        EnglishText = englishOcr,
                        TranslatedText = polishText,
                        QuestName = questName,
                        MatchSource = matchSource
                    };
                    _transcript.Add(transcriptEntry);
                    if (_transcript.Count > MaxTranscriptEntries)
                        _transcript.RemoveAt(0);

                    // Write to file
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    string questTag = !string.IsNullOrEmpty(questName) ? $" [{questName}]" : "";
                    string line = string.IsNullOrEmpty(header)
                        ? $"[{timestamp}]{questTag} {polishText}"
                        : $"[{timestamp}]{questTag} {header}: {polishText}";

                    File.AppendAllText(_currentFile, line + Environment.NewLine);
                }
                catch { /* Don't crash the app for logging failures */ }
            }
        }

        /// <summary>
        /// Get the in-memory session transcript (oldest first).
        /// Returns a snapshot — safe to iterate without locking.
        /// </summary>
        public static List<TranscriptEntry> GetTranscript()
        {
            lock (_lock)
            {
                return new List<TranscriptEntry>(_transcript);
            }
        }

        /// <summary>
        /// Get the last N transcript entries (newest first).
        /// </summary>
        public static List<TranscriptEntry> GetRecentEntries(int count)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _transcript.Count - count);
                int len = Math.Min(count, _transcript.Count);
                var result = new List<TranscriptEntry>(len);
                for (int i = _transcript.Count - 1; i >= start; i--)
                    result.Add(_transcript[i]);
                return result;
            }
        }

        /// <summary>
        /// Get transcript entry count for the current session.
        /// </summary>
        public static int TranscriptCount
        {
            get { lock (_lock) { return _transcript.Count; } }
        }

        /// <summary>
        /// Export the session transcript to a text file.
        /// </summary>
        public static string ExportTranscript(string outputPath = null)
        {
            if (string.IsNullOrEmpty(outputPath))
                outputPath = Path.Combine(_logDir, $"transcript_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");

            lock (_lock)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== Kaption Dialogue Transcript ===");
                sb.AppendLine($"=== Session: {DateTime.Now:yyyy-MM-dd HH:mm} ===");
                sb.AppendLine($"=== Entries: {_transcript.Count} ===");
                sb.AppendLine();

                string currentQuest = null;
                foreach (var entry in _transcript)
                {
                    // Show quest header when quest changes
                    if (entry.QuestName != currentQuest && !string.IsNullOrEmpty(entry.QuestName))
                    {
                        currentQuest = entry.QuestName;
                        sb.AppendLine();
                        sb.AppendLine($"--- {currentQuest} ---");
                        sb.AppendLine();
                    }

                    string ts = entry.Timestamp.ToString("HH:mm:ss");
                    if (!string.IsNullOrEmpty(entry.NpcName))
                        sb.AppendLine($"[{ts}] {entry.NpcName}: {entry.TranslatedText}");
                    else
                        sb.AppendLine($"[{ts}] {entry.TranslatedText}");
                }

                File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
            }

            return outputPath;
        }

        /// <summary>
        /// Clear the in-memory transcript (file log is kept).
        /// </summary>
        public static void ClearTranscript()
        {
            lock (_lock) { _transcript.Clear(); }
        }

        public static string GetCurrentLogPath() => _currentFile;
    }
}
