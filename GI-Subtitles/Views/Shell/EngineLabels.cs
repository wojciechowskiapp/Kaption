using System;
using System.Globalization;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The reader in words people use: the status-bar chip says "GPU · PP-OCRv6" and its tooltip names the graphics card
    /// by its product name. The driver details (vendor and device ids, video memory) belong in the log and in Pomoc ›
    /// Zaawansowane, never on Start or in the chip.
    /// </summary>
    public static class EngineLabels
    {
        /// <summary>"PP-OCRv6 Small" → "PP-OCRv6"; "PP-OCRv4 Mobile (English)" → "PP-OCRv4".</summary>
        public static string ShortModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;
            string m = modelName.Trim();
            int space = m.IndexOf(' ');
            return space > 0 ? m[..space] : m;
        }

        /// <summary>"NVIDIA GeForce RTX 5070 Ti (vendor 0x10DE, device 0x2C05, 15997 MB dedicated)" → "NVIDIA GeForce RTX 5070 Ti".</summary>
        public static string ProductName(string adapterDescription)
        {
            if (string.IsNullOrWhiteSpace(adapterDescription)) return null;
            string a = adapterDescription.Trim();
            int details = a.IndexOf(" (vendor", StringComparison.OrdinalIgnoreCase);
            if (details > 0) a = a[..details];
            return a.Trim();
        }

        /// <summary>The chip: "GPU · PP-OCRv6" (or CPU), just "GPU" when the model is not known yet.</summary>
        public static string Chip(bool onGpu, string modelName, Func<string, string, string> L)
        {
            string device = onGpu ? L("shell_Chip_Gpu", "GPU") : L("shell_Chip_Cpu", "CPU");
            string model = ShortModel(modelName);
            return model.Length == 0 ? device : string.Format(CultureInfo.CurrentCulture, "{0} · {1}", device, model);
        }
    }
}
