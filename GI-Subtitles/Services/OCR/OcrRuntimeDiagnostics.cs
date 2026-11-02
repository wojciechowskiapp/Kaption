using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.OCR
{
    /// <summary>
    /// Recognises an ONNX Runtime native-load failure and gathers the evidence
    /// needed to diagnose one. The runtime caches a failed type initializer for
    /// the life of the process, so such a failure cannot be retried in-process.
    /// </summary>
    internal static class OcrRuntimeDiagnostics
    {
        private const int MaxChainDepth = 32;

        private static readonly string[] RuntimeFiles =
            { "onnxruntime.dll", "onnxruntime_providers_shared.dll", "DirectML.dll" };

        private static readonly string[] VcRuntimeLibraries =
            { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };

        private static readonly Regex Win32HResult = new Regex(
            "0x8007[0-9A-F]{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="error"/> or one of its inner exceptions is a
        /// native-load failure of ONNX Runtime. <paramref name="detail"/> gets the
        /// load error code ("0x8007045A"), or the managed HResult when the message has none.
        /// </summary>
        public static bool IsRuntimeUnavailable(Exception error, out string detail)
        {
            detail = null;
            bool onnxTypeInitializer = false;
            Exception nativeLoad = null;
            int depth = 0;
            for (Exception current = error; current != null && depth < MaxChainDepth;
                 current = current.InnerException, depth++)
            {
                if (current is TypeInitializationException typeInit && MentionsOnnxRuntime(typeInit.TypeName))
                    onnxTypeInitializer = true;
                if (nativeLoad == null &&
                    (current is DllNotFoundException ||
                     current is BadImageFormatException ||
                     current is EntryPointNotFoundException))
                    nativeLoad = current;
            }

            if (nativeLoad == null || !(onnxTypeInitializer || MentionsOnnxRuntime(nativeLoad.Message)))
                return false;

            Match code = Win32HResult.Match(nativeLoad.Message ?? string.Empty);
            detail = code.Success
                ? "0x" + code.Value.Substring(2).ToUpperInvariant()
                : "0x" + nativeLoad.HResult.ToString("X8", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Multi-line report on the runtime files, the libraries they need, the OS
        /// and the GPU. Cheap (no engine construction), never throws, and strips
        /// the user-profile part of every path so it can travel in a crash event.
        /// </summary>
        public static string Probe()
        {
            var report = new StringBuilder();
            try
            {
                string appDir = AppContext.BaseDirectory;
                string ortDir = TryGetManagedRuntimeDir();
                bool separateOrtDir = ortDir != null && !SameDirectory(appDir, ortDir);

                Line(report, "app dir", () => appDir);
                if (separateOrtDir)
                    Line(report, "ONNX Runtime managed dir", () => ortDir);

                foreach (string file in RuntimeFiles)
                {
                    bool tryLoad = file == "DirectML.dll";
                    Line(report, file, () => DescribeRuntimeFile(Path.Combine(appDir, file), tryLoad));
                    if (separateOrtDir)
                        Line(report, file + " (managed dir)",
                            () => DescribeRuntimeFile(Path.Combine(ortDir, file), tryLoad));
                }

                foreach (string library in VcRuntimeLibraries)
                    Line(report, library, () => DescribeLoad(library, reportModule: true));

                Line(report, "OS", () =>
                    $"{RuntimeInformation.OSDescription}, OS arch {RuntimeInformation.OSArchitecture}, " +
                    $"process {RuntimeInformation.ProcessArchitecture}, 64-bit process {Environment.Is64BitProcess}");
                Line(report, "GPU 0", () =>
                    GI_Subtitles.Services.Capture.DxgiScreenCapture.TryGetAdapterName(0, out string name)
                        ? name
                        : "name unavailable");
            }
            catch (Exception ex)
            {
                report.Append("  probe aborted (").Append(ex.GetType().Name).Append(": ")
                      .Append(SanitizePath(ex.Message)).Append(')');
            }
            return report.ToString().TrimEnd();
        }

        /// <summary>Text with every path under the user's profile rewritten to start
        /// with a token, so no Windows user name leaves the machine.</summary>
        public static string SanitizePath(string text) => SanitizePath(
            text,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        internal static string SanitizePath(string text, string localAppData, string userProfile)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = ReplaceRoot(text, localAppData, "%LOCALAPPDATA%");
            return ReplaceRoot(text, userProfile, "%USERPROFILE%");
        }

        private static string ReplaceRoot(string text, string root, string token)
        {
            string prefix = root?.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(prefix)) return text;
            if (text.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return token;
            return text
                .Replace(prefix + "\\", token + "\\", StringComparison.OrdinalIgnoreCase)
                .Replace(prefix + "/", token + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MentionsOnnxRuntime(string text) =>
            text != null && text.IndexOf("onnxruntime", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Line(StringBuilder report, string label, Func<string> describe)
        {
            string value;
            try { value = describe(); }
            catch (Exception ex) { value = $"probe failed ({ex.GetType().Name}: {ex.Message})"; }
            report.Append("  ").Append(label).Append(": ").Append(SanitizePath(value)).Append(Environment.NewLine);
        }

        private static string TryGetManagedRuntimeDir()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(Microsoft.ML.OnnxRuntime.SessionOptions).Assembly.Location);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch
            {
                return null;
            }
        }

        private static bool SameDirectory(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static string DescribeRuntimeFile(string path, bool tryLoad)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return "not present";
            string description = $"{file.Length} bytes, version {FileVersionOf(path)}";
            return tryLoad ? description + "; " + DescribeLoad(path, reportModule: false) : description;
        }

        // NativeLibrary.Load rather than TryLoad: its exception message carries
        // the loader's reason and HRESULT, which TryLoad discards.
        private static string DescribeLoad(string library, bool reportModule)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = NativeLibrary.Load(library);
                string loadedFrom = reportModule ? GetModulePath(handle) : null;
                return loadedFrom == null
                    ? "loads"
                    : $"loads from {loadedFrom}, version {FileVersionOf(loadedFrom)}";
            }
            catch (Exception ex)
            {
                return $"load failed ({ex.GetType().Name}: {ex.Message})";
            }
            finally
            {
                if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
            }
        }

        private static string FileVersionOf(string path)
        {
            string version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        }

        private static string GetModulePath(IntPtr module)
        {
            var buffer = new char[1024];
            uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
            return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr hModule, [Out] char[] lpFilename, uint nSize);
    }
}
