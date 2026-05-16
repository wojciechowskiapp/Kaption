using System.Windows.Media;
using System.IO;
using System.Text;
using System;

namespace Screenshot
{


    public static class DebugLogger
    {
        public static void Log(string message)
        {
            try
            {
                // v2.0 rename: write to the Kaption AppData folder directly instead
                // of the legacy "GI-Subtitles" path. The old path used to cause
                // MigrateAppDataFolder to re-run every launch — Screenshot.dll
                // recreated the folder after App.xaml.cs cleaned it up, so the
                // next launch found "remnants" and merged them again.
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                string filePath = Path.Combine(folder, "screenshot_log.txt");
                string logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

                File.AppendAllText(filePath, logLine, Encoding.UTF8);
            }
            catch { /* Ignore log errors to prevent crashes */ }
        }
    }
    public class ScreenshotOptions
    {
        public ScreenshotOptions()
        {
            BackgroundOpacity = 0.5;
            SelectionRectangleBorderBrush = Brushes.Red;
        }

        /// <summary>
        /// Background opacity when selecting region to capture.
        /// </summary>
        public double BackgroundOpacity { get; set; }

        /// <summary>
        /// Brush used to draw border of selection rectangle.
        /// </summary>
        public Brush SelectionRectangleBorderBrush { get; set; }
    }
}