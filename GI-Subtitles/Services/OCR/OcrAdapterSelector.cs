using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Capture;
using Microsoft.Win32;

namespace GI_Subtitles.Services.OCR
{
    internal readonly record struct OcrAdapterChoice(int Index, string Reason);

    /// <summary>
    /// Picks the GPU DirectML runs OCR on. ONNX Runtime's device id is an <c>EnumAdapters1</c> index, and index 0 is the
    /// adapter driving the display: on a hybrid laptop that is the integrated GPU, so OCR ran there while the discrete
    /// card sat idle (KAP-EF8C-C5CF: an Intel UHD read nothing next to an unused RTX 3050 Ti). Windows' own ranking
    /// decides instead; machines with one GPU, or whose discrete card already drives the display, still get index 0.
    /// </summary>
    internal static class OcrAdapterSelector
    {
        private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        private static readonly Regex GpuPreferencePattern = new(
            @"(?:^|;)\s*GpuPreference\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static int _inUseIndex;
        private static int _adapterListLogged;

        /// <summary>The adapter the current GPU engine runs on (0 until one has loaded).</summary>
        public static int InUseIndex => Volatile.Read(ref _inUseIndex);

        internal static void RecordInUse(int index) => Volatile.Write(ref _inUseIndex, Math.Max(0, index));

        /// <summary>
        /// The first hardware adapter in Windows' ranking, as its <c>EnumAdapters1</c> index. Adapter 0 whenever the
        /// ranking is missing or names nothing usable, which is what Kaption did before.
        /// </summary>
        internal static OcrAdapterChoice Choose(
            IReadOnlyList<GpuAdapterInfo> adapters, IReadOnlyList<long> ranking, DxgiGpuPreference preference)
        {
            if (adapters == null || adapters.Count == 0)
                return new OcrAdapterChoice(0, "no adapters listed, DirectML default");
            if (ranking == null)
                return new OcrAdapterChoice(0, "Windows gave no GPU ranking, DirectML default");

            foreach (long luid in ranking)
            {
                GpuAdapterInfo match = adapters.FirstOrDefault(a => a.Luid == luid);
                if (match == null || match.IsSoftware) continue;
                return new OcrAdapterChoice(match.Index, preference == DxgiGpuPreference.MinimumPower
                    ? "power saving, as set for Kaption in Windows graphics settings"
                    : "Windows' high-performance GPU");
            }
            return new OcrAdapterChoice(0, "no hardware GPU in Windows' ranking, DirectML default");
        }

        /// <summary>The <c>GpuPreference=N;</c> field Windows graphics settings store per app, or null.</summary>
        internal static int? ParseUserGpuPreference(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Match match = GpuPreferencePattern.Match(value);
            return match.Success && int.TryParse(match.Groups[1].Value, out int preference) ? preference : null;
        }

        /// <summary>An explicit "Power saving" for Kaption is honoured; otherwise OCR gets the fast GPU.</summary>
        internal static DxgiGpuPreference PreferenceFor(int? userPreference) =>
            userPreference == 1 ? DxgiGpuPreference.MinimumPower : DxgiGpuPreference.HighPerformance;

        /// <summary>Never throws: any failure answers adapter 0.</summary>
        public static OcrAdapterChoice ChooseForThisProcess()
        {
            try
            {
                int? userPreference = ReadUserGpuPreference();
                DxgiGpuPreference preference = PreferenceFor(userPreference);
                IReadOnlyList<GpuAdapterInfo> adapters = DxgiScreenCapture.EnumerateAdapters();
                OcrAdapterChoice choice = Choose(
                    adapters, DxgiScreenCapture.EnumerateAdapterLuidsByPreference(preference), preference);

                if (Interlocked.Exchange(ref _adapterListLogged, 1) == 0)
                {
                    Logger.Log.Info(
                        $"GPU adapters: {(adapters.Count == 0 ? "none listed" : string.Join(" · ", adapters))}. " +
                        $"Windows graphics setting for Kaption: {DescribeUserPreference(userPreference)}.");
                }
                return choice;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not choose a GPU for OCR, using adapter 0: {ex.Message}");
                return new OcrAdapterChoice(0, "GPU choice failed, DirectML default");
            }
        }

        private static int? ReadUserGpuPreference()
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey);
            // Registry value names compare case-insensitively, as Windows stores the path the user picked.
            return ParseUserGpuPreference(key?.GetValue(exe) as string);
        }

        private static string DescribeUserPreference(int? preference) => preference switch
        {
            null => "none",
            0 => "let Windows decide",
            1 => "power saving",
            2 => "high performance",
            _ => $"unknown ({preference})",
        };
    }
}
