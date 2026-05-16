// ─────────────────────────────────────────────────────────────────────────────
//  AppRestartPrompt.cs
//  ---------------------------------------------------------------------------
//  One reusable "restart Kaption to apply this change" prompt. Wraps
//  ModernDialog.Confirm with the actual process-relaunch sequence so every
//  caller — language switch, game switch, translation-pack update, any
//  future restart trigger — shares the same UX, the same logging, and the
//  same fallback behaviour when the relaunch call itself fails.
//
//  Usage:
//    AppRestartPrompt.PromptAndRestart(
//        owner: this,
//        title: "Translations updated",
//        body:  "Kaption pulled new translation data — restart to apply it.");
//
//  Optional arguments:
//    - details:              extra context shown under the body
//    - restartButtonText:    override "Restart" default
//    - laterButtonText:      override "Later" default
//    - severity:             Info (default) / Question / Warn as appropriate
//    - onDeferred:           callback if the user chose "Later" — lets
//                            callers set a "restart pending" flag
//
//  Return: true if the user accepted the restart (note: by the time this
//  returns, the new process has been launched and Application.Shutdown was
//  called, so callers should treat true as "this method is about to end the
//  process" and not do work after).
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using GI_Subtitles.Views;

namespace GI_Subtitles.Views
{
    internal static class AppRestartPrompt
    {
        /// <summary>
        /// Localisation lookup helper — same pattern as ModernDialog.L so the
        /// helper falls back to English when a key is missing at runtime.
        /// </summary>
        private static string L(string key, string fallback)
            => System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>
        /// Show a "Restart now / Later" confirm. On "Restart now" try to
        /// launch a new instance and call Application.Shutdown. On failure
        /// to relaunch we still shut down — the user asked us to restart;
        /// exiting without relaunch is less surprising than staying open
        /// after clicking Restart.
        /// </summary>
        /// <returns>True when the user chose to restart (process is about to
        /// exit). False if they chose "Later" or dismissed the dialog.</returns>
        public static bool PromptAndRestart(
            Window owner,
            string title,
            string body,
            string details = null,
            string restartButtonText = null,
            string laterButtonText = null,
            DialogSeverity severity = DialogSeverity.Info,
            Action onDeferred = null)
        {
            bool accepted;
            try
            {
                accepted = ModernDialog.Confirm(
                    owner: owner,
                    title: title ?? L("Restart_Prompt_Title", "Restart to apply"),
                    body: body ?? L("Restart_Prompt_Body", "Kaption needs to restart to apply this change."),
                    details: details,
                    primaryText: restartButtonText ?? L("Restart_Prompt_Primary", "Restart now"),
                    secondaryText: laterButtonText ?? L("Restart_Prompt_Secondary", "Later"),
                    severity: severity);
            }
            catch (Exception ex)
            {
                // ModernDialog itself threw — fall back to not restarting.
                // Bubbling would take down whatever caller was relaying an
                // update, which is worse than showing no prompt.
                TryLog($"AppRestartPrompt: Confirm threw — skipping: {ex.Message}");
                return false;
            }

            if (!accepted)
            {
                try { onDeferred?.Invoke(); }
                catch (Exception ex) { TryLog($"AppRestartPrompt: onDeferred threw: {ex.Message}"); }
                return false;
            }

            Restart();
            return true;
        }

        /// <summary>
        /// Relaunch Kaption and exit the current process. Does NOT prompt —
        /// use when the caller has already decided to restart (e.g., auto-
        /// update flow that pre-confirmed).
        /// </summary>
        public static void Restart()
        {
            try
            {
                string exePath = ResolveExePath();
                if (!string.IsNullOrEmpty(exePath))
                {
                    var psi = new ProcessStartInfo(exePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(exePath),
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                TryLog($"AppRestartPrompt: relaunch failed: {ex.Message}");
            }

            try
            {
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                TryLog($"AppRestartPrompt: Shutdown failed: {ex.Message}");
                // Last-resort if Application.Current is already gone.
                Environment.Exit(0);
            }
        }

        /// <summary>
        /// Prefer MainModule.FileName (the full exe path including extension)
        /// over Assembly.Location (which under single-file publish may point
        /// at a bundled .dll that Windows refuses to ShellExecute). Falls
        /// back to the assembly location when MainModule is unavailable.
        /// </summary>
        private static string ResolveExePath()
        {
            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule;
                if (mainModule != null && !string.IsNullOrEmpty(mainModule.FileName))
                    return mainModule.FileName;
            }
            catch { /* some sandboxed hosts throw on MainModule — fall through */ }

            try
            {
                return Assembly.GetEntryAssembly()?.Location ?? Assembly.GetExecutingAssembly().Location;
            }
            catch
            {
                return null;
            }
        }

        private static void TryLog(string message)
        {
            // Reach into MainWindow's log4net instance without adding a new
            // project reference — the logger is a well-known static.
            try
            {
                var logger = log4net.LogManager.GetLogger("LogFileAppender");
                logger?.Warn(message);
            }
            catch
            {
                // Logging must never take down the restart path.
            }
        }
    }
}
