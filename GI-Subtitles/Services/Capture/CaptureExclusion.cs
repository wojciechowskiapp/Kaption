using System;
using System.Runtime.InteropServices;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// Leaves one of Kaption's own windows out of Desktop Duplication, the screen read the app reads the dialogue with,
    /// while the window stays on the monitor and in every other kind of capture (WCA_EXCLUDED_FROM_DDA, Windows 10 1709
    /// and later). Windows 10 22H2 accepts it for the see-through overlay, which it refuses to hide with
    /// WDA_EXCLUDEFROMCAPTURE. An accepted call is not proof that the reader stopped seeing the window: the picture
    /// Desktop Duplication hands out changes only where the screen is drawn again, so <see cref="OverlayReadCheck"/>
    /// decides. Measurements: .plan/research/s68/OVERLAY-OVER-TEXT.md.
    /// </summary>
    public static class CaptureExclusion
    {
        private const int WcaExcludedFromDda = 24;
        private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttribData
        {
            public int Attribute;
            public IntPtr Data;
            public nuint Size;
        }

        // Exported by user32.dll without a header; a Windows that lacks it answers with EntryPointNotFoundException.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        /// <summary>
        /// Asks Windows to leave the window out of Desktop Duplication (or to put it back). False when Windows refuses
        /// or does not know the attribute.
        /// </summary>
        /// <param name="nudge">
        /// Also tell Windows the window's frame changed. Over a screen that is not being redrawn the attribute alone
        /// did not reach the frames within three seconds in one try of five (probe 6).
        /// </param>
        public static bool TrySetOutOfDesktopDuplication(IntPtr hwnd, bool on, bool nudge = true)
        {
            if (hwnd == IntPtr.Zero) return false;
            IntPtr value = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(value, on ? 1 : 0);
                var data = new WindowCompositionAttribData { Attribute = WcaExcludedFromDda, Data = value, Size = sizeof(int) };
                if (!SetWindowCompositionAttribute(hwnd, ref data)) return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(value);
            }
            if (nudge)
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            return true;
        }
    }
}
