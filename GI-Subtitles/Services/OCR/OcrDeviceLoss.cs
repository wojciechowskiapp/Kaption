using System;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Recognises a read that failed because Windows took the GPU device away (driver update or reset, sleep, a GPU
    /// switch; Sentry KAPTION-DESKTOP-1). The DirectML sessions are then dead for good and must be rebuilt.
    /// </summary>
    internal static class OcrDeviceLoss
    {
        // DXGI_ERROR_DEVICE_REMOVED, _DEVICE_HUNG, _DEVICE_RESET, _DRIVER_INTERNAL_ERROR: each means the D3D device is
        // gone. ONNX Runtime puts the code in the message as bare hex next to Windows' localized text.
        private static readonly int[] DeviceGoneResults =
        {
            unchecked((int)0x887A0005), unchecked((int)0x887A0006), unchecked((int)0x887A0007), unchecked((int)0x887A0020),
        };

        private static readonly Regex DeviceGoneCode = new(
            @"(?<![0-9A-Fa-f])(?:0x)?887A00(?:05|06|07|20)(?![0-9A-Fa-f])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsDeviceLost(Exception error)
        {
            if (error == null) return false;
            if (Array.IndexOf(DeviceGoneResults, error.HResult) >= 0) return true;
            if (!string.IsNullOrEmpty(error.Message) && DeviceGoneCode.IsMatch(error.Message)) return true;
            if (error is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    if (IsDeviceLost(inner)) return true;
                return false;
            }
            return IsDeviceLost(error.InnerException);
        }
    }

    internal enum OcrDeviceLossAction
    {
        RebuildOnGpu,
        RebuildOnProcessor,
    }

    /// <summary>
    /// After a lost device: rebuild on the GPU once; a second loss inside <see cref="Cooldown"/> means the driver is
    /// not settled, so the rest of the session reads on the processor instead of looping. Pure, clock injected.
    /// </summary>
    internal sealed class OcrDeviceLossPolicy
    {
        public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

        private readonly object _gate = new();
        private DateTime? _lastGpuRebuildUtc;

        public OcrDeviceLossAction Decide(DateTime utcNow)
        {
            lock (_gate)
            {
                // A clock that went backwards counts as inside the cooldown: the processor is the safe side.
                if (_lastGpuRebuildUtc is DateTime last && (utcNow < last || utcNow - last < Cooldown))
                    return OcrDeviceLossAction.RebuildOnProcessor;
                _lastGpuRebuildUtc = utcNow;
                return OcrDeviceLossAction.RebuildOnGpu;
            }
        }
    }
}
