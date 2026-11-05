using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace GI_Test
{
    /// <summary>
    /// GI-Test's output has no DirectML.dll on purpose (the OCR goldens stay on the CPU), so Windows loads its 2021
    /// inbox copy and DirectML fails with 887A0004. The GPU tests opt in with KAPTION_TEST_DIRECTML=1 and load the
    /// package's copy first. Run them alone, before anything binds the inbox DLL:
    /// <c>KAPTION_TEST_DIRECTML=1 dotnet test GI-Test/GI-Test.csproj --filter "TestCategory=DirectML"</c>.
    /// </summary>
    internal static class DirectMLTestSupport
    {
        private static readonly object Gate = new();
        private static bool? _enabled;
        private static string _why;

        public static bool TryEnable(out string why)
        {
            lock (Gate)
            {
                if (_enabled == null) _enabled = Load(out _why);
                why = _why;
                return _enabled.Value;
            }
        }

        private static bool Load(out string why)
        {
            if (Environment.GetEnvironmentVariable("KAPTION_TEST_DIRECTML") != "1")
            {
                why = "Set KAPTION_TEST_DIRECTML=1 (and filter TestCategory=DirectML) to run the DirectML tests.";
                return false;
            }

            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            string nugetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "microsoft.ai.directml");
            string path = new[]
                {
                    Path.Combine(repoRoot, "GI-Subtitles", "bin", "Debug", "DirectML.dll"),
                    Path.Combine(repoRoot, "GI-Subtitles", "bin", "Release", "DirectML.dll"),
                }
                .Concat(Directory.Exists(nugetRoot)
                    ? Directory.GetDirectories(nugetRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, "bin", "x64-win", "DirectML.dll"))
                    : Enumerable.Empty<string>())
                .FirstOrDefault(File.Exists);
            if (path == null)
            {
                why = "No package DirectML.dll found (build GI-Subtitles first).";
                return false;
            }

            NativeLibrary.Load(path);
            why = null;
            return true;
        }
    }
}
