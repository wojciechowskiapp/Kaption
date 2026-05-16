using System;

namespace GI_Subtitles.Core.Logging
{
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }

        public LogEntry(DateTime timestamp, string level, string message)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
        }

        public string TimeString => Timestamp.ToString("HH:mm:ss.fff");
    }
}
