using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Input;

#if DEBUG
// What the suite checks before it starts a Kaption.exe: the mode below is compiled into this build.
[assembly: System.Reflection.AssemblyMetadata(GI_Subtitles.Common.E2EMode.BuildMarker, "true")]
#endif

namespace GI_Subtitles.Common
{
    /// <summary>
    /// The desktop end-to-end suite's mode (Kaption.DesktopE2E): set <see cref="DataDirVariable"/> to an absolute folder
    /// outside the real profile and the app keeps everything there, skips the sign-in and first-run windows, registers
    /// no global shortcuts and never touches another Kaption. It is compiled into Debug builds only; a shipped build is
    /// a Release publish and cannot enter it.
    /// </summary>
    public static class E2EMode
    {
        public const string DataDirVariable = "KAPTION_E2E_DATA_DIR";

        /// <summary>The AssemblyMetadata key a build carries when the mode is compiled in.</summary>
        public const string BuildMarker = "KaptionE2EMode";

#if DEBUG
        static E2EMode()
        {
            (DataDir, Refusal) = Decide(Environment.GetEnvironmentVariable(DataDirVariable), LiveProfile());
        }

        /// <summary>The isolated data folder, or null outside the test mode.</summary>
        public static string DataDir { get; }

        /// <summary>Why a set variable was not accepted (the app then quits instead of opening the real profile).</summary>
        public static string Refusal { get; }
#else
        public static string DataDir => null;
        public static string Refusal => null;
#endif

        public static bool IsActive => DataDir != null;

        /// <summary>%APPDATA%\Kaption, the profile the suite must never use.</summary>
        internal static string LiveProfile() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption");

        /// <summary>
        /// A plain drive-letter folder (<c>X:\...</c>) that is not the real profile or inside it. The two are compared by
        /// where they are, not how they are spelled: each path's deepest existing folder is opened and asked for its final
        /// volume path, so links, junctions, <c>subst</c> drives, mount points and short names all lead to one answer.
        /// Unset: no mode and no refusal. Anything else is refused, so a mistyped variable never falls back to the profile.
        /// </summary>
        internal static (string DataDir, string Refusal) Decide(string value, string liveProfile)
        {
            if (value == null) return (null, null);
            // Set but blank is a mistake, not "off": refused like any other unusable folder.
            string trimmed = value.Trim();
            if (!IsPlainDrivePath(trimmed))
                return (null, $"{DataDirVariable} must be a plain drive folder like C:\\folder, not \"{trimmed}\".");
            string dir = Path.GetFullPath(trimmed);
            string candidate, live;
            try
            {
                if (new DriveInfo(dir).DriveType == DriveType.Network)
                    return (null, $"{DataDirVariable} is on a network drive: \"{dir}\".");
                candidate = FinalLocation(dir);
                live = FinalLocation(liveProfile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Fail closed: a location that cannot be pinned down might be the profile.
                return (null, $"{DataDirVariable} could not be checked against the real profile: {ex.Message}");
            }
            if (IsSameOrInside(candidate, live))
                return (null, $"{DataDirVariable} points into the real profile ({liveProfile}).");
            return (dir, null);
        }

        /// <summary>
        /// <c>X:\...</c> only: no <c>\\?\</c>, <c>\\.\</c> or UNC spellings, no relative or drive-relative path, and no colon
        /// after the drive letter's. A second colon names an NTFS stream: <c>Kaption::$INDEX_ALLOCATION</c> is the profile
        /// folder itself, and creating it creates the profile.
        /// </summary>
        internal static bool IsPlainDrivePath(string path) =>
            path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
            (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar) &&
            path.IndexOf(':', 2) < 0;

        private static bool IsSameOrInside(string path, string root)
        {
            string p = Path.TrimEndingDirectorySeparator(path), r = Path.TrimEndingDirectorySeparator(root);
            // A volume root keeps its separator when trimmed: exactly one before the descendant check.
            string prefix = Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar;
            return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The final <c>\\?\Volume{guid}\...</c> path of <paramref name="path"/>'s deepest existing folder, with the part
        /// that does not exist yet appended as written. Only "not found" ends the walk; denied access throws.
        /// </summary>
        internal static string FinalLocation(string path)
        {
            string existing = Path.GetFullPath(path), tail = "";
            while (true)
            {
                try
                {
                    File.GetAttributes(existing);
                    break;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    string parent = Path.GetDirectoryName(existing)
                                    ?? throw new IOException($"No part of \"{path}\" exists.");
                    tail = tail.Length == 0 ? Path.GetFileName(existing) : Path.Combine(Path.GetFileName(existing), tail);
                    existing = parent;
                }
            }
            using SafeFileHandle handle = NativeMethods.CreateFileW(existing, 0,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, NativeMethods.BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), existing);
            var buffer = new char[NativeMethods.MaxPath];
            uint length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NativeMethods.VolumeNameGuid);
            if (length == 0 || length >= buffer.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), existing);
            string final = new string(buffer, 0, (int)length);
            return tail.Length == 0 ? final : Path.Combine(final, tail);
        }

        private static class NativeMethods
        {
            public const uint BackupSemantics = 0x02000000;   // FILE_FLAG_BACKUP_SEMANTICS: needed to open a folder
            public const uint VolumeNameGuid = 0x1;           // VOLUME_NAME_GUID, FILE_NAME_NORMALIZED
            public const int MaxPath = 32_768;                // the longest extended-length path

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFileW(string fileName, uint access, FileShare share, IntPtr security,
                                                            FileMode mode, uint flags, IntPtr template);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
        }

        /// <summary>Signalled by the suite to quit the app; per process, in the desktop session's namespace.</summary>
        public static string QuitEventName(int processId) => $@"Local\Kaption-E2E-Quit-{processId}";

        /// <summary>
        /// UI Automation never types or clicks, so keys and clicks from the desktop are dropped while the suite drives the
        /// app (as GI-Test's RenderTestKit does for its windows).
        /// </summary>
        public static void DropDesktopInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
                e.Cancel();
        }
    }
}
