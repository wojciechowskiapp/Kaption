using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Startup
{
    /// <summary>The command-line switches Kaption itself understands (Velopack's <c>--veloapp-*</c> hooks never reach these).</summary>
    public static class StartupArguments
    {
        /// <summary>Start in the tray: icon and overlay come up, the app window stays closed. Written by "Start with Windows".</summary>
        public const string Tray = "--tray";

        public static bool IsTrayStart(IEnumerable<string> args) =>
            args != null && args.Any(a => string.Equals(a?.Trim(), Tray, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The values under the current user's Run key, behind a seam so tests never touch the real registry.</summary>
    public interface IRunKeyStore
    {
        /// <summary>Every string value of the key: name → command. Empty when the key cannot be read.</summary>
        IReadOnlyDictionary<string, string> ReadAll();
        void Set(string name, string command);
        void Delete(string name);
    }

    /// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
    public sealed class RegistryRunKeyStore : IRunKeyStore
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public IReadOnlyDictionary<string, string> ReadAll()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key == null) return values;
            foreach (string name in key.GetValueNames())
            {
                // Unexpanded: a REG_EXPAND_SZ entry of another program is compared as written, never rewritten.
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                    values[name] = command;
            }
            return values;
        }

        public void Set(string name, string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(name, command, RegistryValueKind.String);
        }

        public void Delete(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>What <see cref="WindowsStartup.Plan"/> decided: the values to remove, and the one to write (null: none).</summary>
    public sealed record RunKeyPlan(IReadOnlyList<string> Delete, string SetCommand)
    {
        public bool IsNoOp => Delete.Count == 0 && SetCommand == null;
    }

    /// <summary>
    /// "Start with Windows" (Config <see cref="SettingKey"/>, off unless the player ticks it). On: one Run value named
    /// <see cref="ValueName"/> holding the quoted executable and <c>--tray</c>, repaired at every launch. Off: no value
    /// of ours is left, the ones older builds wrote included.
    /// </summary>
    /// <remarks>
    /// Older builds wrote <c>Assembly.Location</c> (the .dll on .NET 5+, which Windows cannot run), unquoted, under the
    /// process name. Velopack keeps the installed app in <c>%LocalAppData%\Kaption\current\</c> and swaps that folder's
    /// content on update, so the executable's own path is the stable one to register.
    /// </remarks>
    public static class WindowsStartup
    {
        public const string SettingKey = "AutoStart";
        public const string ValueName = "Kaption";

        /// <summary>Value names older builds may have used (the process name before the rename to Kaption).</summary>
        private static readonly string[] LegacyValueNames = { "GI-Subtitles" };

        /// <summary>File names (no extension) of Kaption's own binaries: a Run value that starts one of these is ours.</summary>
        private static readonly string[] OwnBinaryStems = { "Kaption", "GI-Subtitles" };

        public static string CommandFor(string executablePath) => $"\"{executablePath}\" {StartupArguments.Tray}";

        /// <summary>
        /// The executable to register, or null when there is none to point Windows at: under <c>dotnet Kaption.dll</c>
        /// the process is dotnet.exe, so the app host beside the assembly is used when it exists.
        /// </summary>
        public static string ResolveExecutable(string processPath, string entryAssemblyPath, Func<string, bool> fileExists)
        {
            if (IsOwnBinary(processPath, ".exe")) return processPath;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath)) return null;
            string host = Path.ChangeExtension(entryAssemblyPath, ".exe");
            return IsOwnBinary(host, ".exe") && fileExists != null && fileExists(host) ? host : null;
        }

        /// <summary>The program a Run command starts: the quoted part, else the text up to the first .exe/.dll.</summary>
        public static string TargetOf(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            string c = command.Trim();
            if (c[0] == '"')
            {
                int close = c.IndexOf('"', 1);
                return close > 0 ? c.Substring(1, close - 1) : c.Substring(1);
            }
            foreach (string ext in new[] { ".exe", ".dll" })
            {
                for (int at = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = c.IndexOf(ext, at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    int end = at + ext.Length;
                    if (end == c.Length || char.IsWhiteSpace(c[end])) return c.Substring(0, end);
                }
            }
            return c;
        }

        /// <summary>True for a Run value Kaption wrote: by name, or because it starts one of Kaption's binaries.</summary>
        public static bool IsOurs(string name, string command)
        {
            if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)) return true;
            if (LegacyValueNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            string target = TargetOf(command);
            return IsOwnBinary(target, ".exe") || IsOwnBinary(target, ".dll");
        }

        private static bool IsOwnBinary(string path, string extension)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string file;
            try { file = Path.GetFileName(path.Trim()); }
            catch (ArgumentException) { return false; }
            return string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase)
                && OwnBinaryStems.Contains(Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The changes that make the Run key match the setting. On, with an executable: our stale values go (old name,
        /// a .dll target, no quotes) and <see cref="ValueName"/> is written unless it already holds the exact command.
        /// On, without one: nothing changes, because there is nothing right to write. Off: every value of ours goes.
        /// </summary>
        public static RunKeyPlan Plan(bool enabled, string executablePath, IReadOnlyDictionary<string, string> values)
        {
            values ??= new Dictionary<string, string>();
            var ours = values.Where(v => IsOurs(v.Key, v.Value)).ToList();
            if (!enabled)
                return new RunKeyPlan(ours.Select(v => v.Key).ToList(), null);
            if (string.IsNullOrWhiteSpace(executablePath))
                return new RunKeyPlan(Array.Empty<string>(), null);

            string wanted = CommandFor(executablePath);
            var stale = ours.Where(v => !string.Equals(v.Key, ValueName, StringComparison.OrdinalIgnoreCase)).Select(v => v.Key).ToList();
            bool current = ours.Any(v => string.Equals(v.Key, ValueName, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(v.Value, wanted, StringComparison.Ordinal));
            return new RunKeyPlan(stale, current ? null : wanted);
        }

        /// <summary>Plans against <paramref name="store"/> and carries the plan out; returns it for the log and the tests.</summary>
        public static RunKeyPlan Apply(bool enabled, string executablePath, IRunKeyStore store)
        {
            var plan = Plan(enabled, executablePath, store.ReadAll());
            foreach (string name in plan.Delete) store.Delete(name);
            if (plan.SetCommand != null) store.Set(ValueName, plan.SetCommand);
            return plan;
        }

        /// <summary>
        /// Uninstall leaves no Run entry behind. Runs inside Velopack's uninstall hook, before the logger exists and with
        /// 30 seconds to live: no logging, and nothing may escape.
        /// </summary>
        public static void RemoveForUninstall()
        {
            try
            {
                string exe = ResolveExecutable(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location, File.Exists);
                RemoveForUninstall(E2EMode.IsActive, exe, () => new RegistryRunKeyStore());
            }
            catch { /* the uninstall goes on */ }
        }

        /// <summary>
        /// The same guard as <see cref="ApplySetting"/>: the real key is opened (<paramref name="openStore"/>) only by
        /// Kaption's own executable outside the end-to-end test mode.
        /// </summary>
        internal static void RemoveForUninstall(bool e2eMode, string executablePath, Func<IRunKeyStore> openStore)
        {
            if (!MayTouchRunKey(e2eMode, executablePath)) return;
            try { Apply(enabled: false, executablePath: null, openStore()); }
            catch { /* the uninstall goes on; a leftover entry points at nothing and Windows skips it */ }
        }

        /// <summary>Only Kaption's own executable, outside the end-to-end test mode, reads or writes the Run key.</summary>
        public static bool MayTouchRunKey(bool e2eMode, string executablePath) => !e2eMode && !string.IsNullOrWhiteSpace(executablePath);

        /// <summary>
        /// Makes the real Run key match the setting, now. Called at launch (the repair) and whenever the setting changes.
        /// Never throws: a locked-down registry costs the convenience, not the app.
        /// </summary>
        public static void ApplySetting(bool enabled)
        {
            try
            {
                string exe = ResolveExecutable(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location, File.Exists);
                if (!MayTouchRunKey(E2EMode.IsActive, exe))
                {
                    // The Run key belongs to the player's own Kaption. The end-to-end suite has its own settings (off),
                    // and a test host or tool that loads this assembly is not Kaption: neither may remove the entry.
                    Logger.Log.Info("Start with Windows: not the player's Kaption (test mode, or started through another program); the Run entry is left as it is.");
                    return;
                }
                var plan = Apply(enabled, exe, new RegistryRunKeyStore());
                if (!plan.IsNoOp)
                    Logger.Log.Info($"Start with Windows: {(enabled ? "on" : "off")}; removed {plan.Delete.Count} entr{(plan.Delete.Count == 1 ? "y" : "ies")}" +
                                    (plan.SetCommand != null ? $", registered {plan.SetCommand}." : "."));
            }
            catch (Exception ex)
            {
                // Warn, not Error: a policy that locks the Run key is the machine's state, nothing a person can fix from Sentry.
                Logger.Log.Warn($"Start with Windows: could not update the Run entry: {ex.Message}");
            }
        }
    }
}
