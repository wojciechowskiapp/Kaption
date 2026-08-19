// ─────────────────────────────────────────────────────────────────────────────
//  DiagnosticBundle.cs
//  ---------------------------------------------------------------------------
//  Builds a single .zip holding everything needed to diagnose a user's problem,
//  so a support conversation can start with the answer instead of five rounds
//  of "what version are you on / did you pick a region / what GPU".
//
//  Design notes:
//
//  * Every artifact is produced by its own collector and each one is caught
//    individually. A bundle missing one section is still useful; a bundle that
//    failed to build because one probe threw is not.
//
//  * Everything textual goes through LogRedactor before it is written. There is
//    no path into the zip that skips it.
//
//  * The zip is written to disk and the path returned. Uploading is a separate
//    concern — saving first means the user always ends up with a file they can
//    send by hand, which is what they need when the network or their licence is
//    the thing that is broken.
//
//  * Screenshots are never included. The OCR frame dumps written when
//    Config["Debug"] is on are pictures of the user's screen, and the whole
//    point of the "what's included" list is that it stays honest.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Logging;
using GI_Subtitles.Services.Data;

namespace GI_Subtitles.Services.Diagnostics
{
    /// <summary>Outcome of a bundle build.</summary>
    public sealed class DiagnosticBundleResult
    {
        public string FilePath { get; set; }
        public string SupportId { get; set; }
        public long SizeBytes { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    public static class DiagnosticBundle
    {
        /// <summary>
        /// Bump when the layout of the zip changes, so a future reader can tell
        /// what it is looking at.
        /// </summary>
        private const int BundleFormatVersion = 1;

        /// <summary>
        /// Cap for any single log file. Enough to cover a long session while
        /// keeping the bundle small enough to paste into a chat window.
        /// Oversized files are tail-read: the end holds whatever just went
        /// wrong, which is what we are looking for.
        /// </summary>
        private const long MaxSingleFileBytes = 4L * 1024 * 1024;

        /// <summary>Dialogue transcripts are user content, so only the last two days ship.</summary>
        private const int DialogueLogDays = 2;

        private static readonly Dictionary<string, Func<string>> StateProviders =
            new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly object ProvidersGate = new object();

        /// <summary>
        /// Lets a subsystem contribute a line of runtime state without this
        /// class having to know it exists. MainWindow registers engine status,
        /// the OCR region and so on at startup.
        /// </summary>
        public static void RegisterStateProvider(string label, Func<string> provider)
        {
            if (string.IsNullOrWhiteSpace(label) || provider == null) return;
            lock (ProvidersGate) StateProviders[label] = provider;
        }

        /// <summary>
        /// Overrides where logs are read from and bundles are written. Null in
        /// production, where everything lives under %APPDATA%\Kaption.
        ///
        /// This exists so tests can run against a temp directory they own
        /// instead of whatever happens to be on the machine — the collector's
        /// output otherwise depends on whether Kaption has ever run here, which
        /// makes it pass locally and prove nothing on a clean CI runner.
        /// </summary>
        public static string RootOverride { get; set; }

        private static string DataRoot => RootOverride ?? GameDataPaths.Root;

        /// <summary>Where finished bundles are written.</summary>
        public static string OutputDirectory => Path.Combine(DataRoot, "diagnostics");

        /// <summary>
        /// Builds a bundle and returns where it landed. Runs off the UI thread;
        /// the caller is expected to show progress and keep the window alive.
        /// </summary>
        public static Task<DiagnosticBundleResult> CreateAsync(
            string userNote, CancellationToken ct = default)
        {
            return Task.Run(() => Create(userNote, ct), ct);
        }

        private static DiagnosticBundleResult Create(string userNote, CancellationToken ct)
        {
            var result = new DiagnosticBundleResult { SupportId = NewSupportId() };

            string staging = Path.Combine(
                Path.GetTempPath(), "kaption-diag-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(staging);

            try
            {
                CollectAll(staging, userNote, result, ct);

                Directory.CreateDirectory(OutputDirectory);
                string zipPath = Path.Combine(
                    OutputDirectory,
                    string.Format(CultureInfo.InvariantCulture, "Kaption-diagnostics-{0}-{1}.zip",
                        DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture),
                        result.SupportId));

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, false);

                result.FilePath = zipPath;
                result.SizeBytes = new FileInfo(zipPath).Length;
                Logger.Log.Info(
                    $"Diagnostics: bundle {result.SupportId} written to {zipPath} ({result.SizeBytes / 1024} KB).");
                return result;
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        private static void CollectAll(
            string staging, string userNote, DiagnosticBundleResult result, CancellationToken ct)
        {
            // Ordered roughly by how useful each part is when opening the zip.
            Collect(result, "manifest", () => WriteText(
                Path.Combine(staging, "manifest.txt"), BuildManifest(result)));

            Collect(result, "note", () => WriteText(
                Path.Combine(staging, "user-note.txt"),
                string.IsNullOrWhiteSpace(userNote) ? "(the user did not describe the problem)" : userNote));

            Collect(result, "environment", () => WriteText(
                Path.Combine(staging, "environment.txt"), BuildEnvironment()));

            Collect(result, "state", () => WriteText(
                Path.Combine(staging, "state.txt"), BuildRuntimeState()));

            Collect(result, "debug-history", () => WriteText(
                Path.Combine(staging, "logs", "debug-recent.log"), BuildRingBufferDump()));

            ct.ThrowIfCancellationRequested();

            Collect(result, "config", () => CopyConfigFiles(staging, result));
            Collect(result, "logs", () => CopyLogFiles(staging, result, ct));
        }

        private static void Collect(DiagnosticBundleResult result, string label, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // One failed probe must not cost us the whole bundle.
                result.Notes.Add($"{label}: failed ({ex.GetType().Name}: {ex.Message})");
                Logger.Log.Warn($"Diagnostics: collector '{label}' failed.", ex);
            }
        }

        // ── artifacts ────────────────────────────────────────────────────────

        private static string BuildManifest(DiagnosticBundleResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Kaption diagnostic bundle");
            sb.AppendLine("=========================");
            sb.AppendLine($"Bundle format   : {BundleFormatVersion}");
            sb.AppendLine($"Support ID      : {result.SupportId}");
            sb.AppendLine($"Created (local) : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Created (UTC)   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"App version     : {AppVersion()}");
            sb.AppendLine($"Detailed logging: {(Logger.IsDetailedLoggingEnabled() ? "on" : "off")}");
            sb.AppendLine($"Verbose OCR dump: {Config.Get("Debug", false)}");
            sb.AppendLine();
            sb.AppendLine("Personal data has been removed: Windows usernames, session tokens,");
            sb.AppendLine("secret-shaped config values and email local parts are masked.");
            return sb.ToString();
        }

        private static string BuildEnvironment()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Environment");
            sb.AppendLine("===========");
            sb.AppendLine($"OS              : {Environment.OSVersion} (64-bit OS: {Environment.Is64BitOperatingSystem})");
            sb.AppendLine($"Runtime         : {Environment.Version}");
            sb.AppendLine($"Processors      : {Environment.ProcessorCount}");
            sb.AppendLine($"System locale   : {CultureInfo.CurrentCulture.Name}");
            sb.AppendLine($"UI locale       : {CultureInfo.CurrentUICulture.Name}");
            sb.AppendLine($"Working set     : {Environment.WorkingSet / (1024 * 1024)} MB");
            sb.AppendLine($"Process 64-bit  : {Environment.Is64BitProcess}");
            sb.AppendLine();

            sb.AppendLine("Displays");
            sb.AppendLine("--------");
            try
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0}{1}: bounds {2}x{3} at ({4},{5}), working area {6}x{7}",
                        screen.DeviceName,
                        screen.Primary ? " [primary]" : "",
                        screen.Bounds.Width, screen.Bounds.Height,
                        screen.Bounds.X, screen.Bounds.Y,
                        screen.WorkingArea.Width, screen.WorkingArea.Height));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  (unavailable: {ex.Message})");
            }

            return sb.ToString();
        }

        private static string BuildRuntimeState()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Runtime state");
            sb.AppendLine("=============");

            KeyValuePair<string, Func<string>>[] providers;
            lock (ProvidersGate) providers = StateProviders.ToArray();

            if (providers.Length == 0)
            {
                sb.AppendLine("(no subsystem reported state — the app may not have finished starting)");
            }

            foreach (var provider in providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                string value;
                try { value = provider.Value() ?? "(null)"; }
                catch (Exception ex) { value = $"(failed: {ex.Message})"; }
                sb.AppendLine($"{provider.Key,-24}: {value}");
            }

            return sb.ToString();
        }

        private static string BuildRingBufferDump()
        {
            var entries = LogBuffer.Snapshot();
            var sb = new StringBuilder();
            sb.AppendLine($"In-memory log history ({entries.Length} entries)");
            sb.AppendLine("Includes DEBUG records, which are not written to app.log.");
            sb.AppendLine(new string('-', 60));

            foreach (var entry in entries)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] {2}",
                    entry.Timestamp, entry.Level, entry.Message));
            }
            return sb.ToString();
        }

        private static void CopyConfigFiles(string staging, DiagnosticBundleResult result)
        {
            // Two config sources are overlaid at runtime (app directory first,
            // then %APPDATA%). Shipping both makes a stale app-directory copy
            // silently overriding the real one visible instead of baffling.
            var sources = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Config.json"),
                Path.Combine(DataRoot, "Config.json"),
            };

            bool any = false;
            for (int i = 0; i < sources.Length; i++)
            {
                if (!File.Exists(sources[i])) continue;
                any = true;
                string label = i == 0 ? "Config.appdir.json" : "Config.appdata.json";
                WriteText(Path.Combine(staging, "config", label), ReadTextShared(sources[i], MaxSingleFileBytes));
            }

            if (!any) result.Notes.Add("config: no Config.json found on disk");
        }

        private static void CopyLogFiles(string staging, DiagnosticBundleResult result, CancellationToken ct)
        {
            string root = DataRoot;
            var wanted = new List<string>
            {
                Path.Combine(root, "app.log"),
                Path.Combine(root, "app.log.1"),
                Path.Combine(root, "crash.log"),
                // Unbounded and unrotated, so it is tail-read like everything
                // else rather than trusted to be a sensible size.
                Path.Combine(root, "screenshot_log.txt"),
            };

            wanted.AddRange(RecentDialogueLogs(root));

            int copied = 0;
            foreach (string source in wanted)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(source)) continue;

                try
                {
                    long size = new FileInfo(source).Length;
                    string text = ReadTextShared(source, MaxSingleFileBytes);
                    if (size > MaxSingleFileBytes)
                    {
                        // Say so rather than letting the reader chase a gap we created.
                        text = $"[trimmed: kept the last {MaxSingleFileBytes / 1024} KB of {size / 1024} KB]"
                               + Environment.NewLine + text;
                        result.Notes.Add(
                            $"logs: {Path.GetFileName(source)} trimmed from {size / 1024} KB");
                    }
                    WriteText(Path.Combine(staging, "logs", Path.GetFileName(source)), text);
                    copied++;
                }
                catch (Exception ex)
                {
                    // Rotation can delete a file between the check and the read.
                    result.Notes.Add($"logs: skipped {Path.GetFileName(source)} ({ex.Message})");
                }
            }

            if (copied == 0)
            {
                // A fresh install with no logs is itself a finding, so record it
                // instead of shipping an empty folder that looks like a bug.
                result.Notes.Add("logs: no log files found on disk");
                WriteText(Path.Combine(staging, "logs", "README.txt"),
                    "No log files were present when this bundle was built.");
            }
        }

        private static IEnumerable<string> RecentDialogueLogs(string root)
        {
            string dir = Path.Combine(root, "logs");
            if (!Directory.Exists(dir)) yield break;

            var recent = Directory.GetFiles(dir, "dialogue_*.txt")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(DialogueLogDays);

            foreach (string file in recent) yield return file;
        }

        // ── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a file that another part of the process may be writing to, so
        /// the share mode is permissive. Files over <paramref name="maxBytes"/>
        /// are tail-read.
        /// </summary>
        private static string ReadTextShared(string path, long maxBytes)
        {
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > maxBytes)
                {
                    stream.Seek(-maxBytes, SeekOrigin.End);
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static void WriteText(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, LogRedactor.Scrub(content) ?? string.Empty, Encoding.UTF8);
        }

        /// <summary>
        /// Short, unambiguous when read aloud or typed into a chat window.
        /// Generated locally so it exists even when an upload fails and the
        /// user ends up sending the file by hand.
        /// </summary>
        private static string NewSupportId()
        {
            string hex = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            return $"KAP-{hex.Substring(0, 4)}-{hex.Substring(4, 4)}";
        }

        private static string AppVersion()
        {
            try { return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) { Logger.Log.Warn($"Diagnostics: could not clean staging dir {path}: {ex.Message}"); }
        }
    }
}
