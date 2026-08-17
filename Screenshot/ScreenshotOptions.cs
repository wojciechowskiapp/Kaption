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
                // Screenshot diagnostics belong exclusively to Kaption.
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
