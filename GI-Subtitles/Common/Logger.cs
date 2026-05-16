using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Logging;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// Logger utility class
    /// </summary>
    public static class Logger
    {
        public static log4net.ILog Log = log4net.LogManager.GetLogger("LogFileAppender");

        public static void RegisterInMemoryAppender()
        {
            var appender = new InMemoryAppender();
            appender.ActivateOptions();
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            hierarchy.Root.AddAppender(appender);
        }

        /// <summary>
        /// Attach the Sentry/GlitchTip forwarding appender to the log4net root.
        /// From this point on every <c>Logger.Log.Error(...)</c> (and Fatal)
        /// is automatically captured by <see cref="SentryAppender"/> and
        /// shipped to GlitchTip — no per-call-site changes required. WARN
        /// and below stay local only.
        ///
        /// Must be called AFTER
        /// <see cref="GI_Subtitles.Services.Observability.CrashReportingService.Initialize"/>
        /// because the appender consults <c>CrashReportingService.IsEnabled</c>
        /// on every Append; calling before init means the first few errors
        /// would short-circuit on consent being unknown.
        /// </summary>
        public static void RegisterSentryAppender()
        {
            var appender = new SentryAppender();
            appender.ActivateOptions();
            var hierarchy = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            hierarchy.Root.AddAppender(appender);
        }

        /// <summary>
        /// Runtime override for the log4net root level. Shipped default is
        /// INFO (see app.config). Users who need debug-level logs for
        /// troubleshooting can set the <c>LogLevel</c> key in Config.json
        /// to DEBUG (or TRACE / WARN / ERROR / ALL / OFF) — we then
        /// re-parse it here and bump the root level without requiring a
        /// rebuild. Unrecognised values are logged and ignored, leaving
        /// the config-file default in force.
        ///
        /// Called once at startup from App.xaml.cs after the in-memory
        /// appender is attached. Config.Watch=true on the file appender
        /// means a later edit of app.config would re-run XmlConfigurator
        /// and overwrite our override; that's acceptable because the
        /// file-edit path is a developer workflow and they're the ones
        /// asking for DEBUG anyway.
        /// </summary>
        public static void ApplyConfiguredLevel()
        {
            string raw = Config.Get<string>("LogLevel", null);
            if (string.IsNullOrWhiteSpace(raw)) return; // keep config-file default

            var repo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            var level = repo.LevelMap[raw.Trim()];
            if (level == null)
            {
                Log.Warn($"Logger: ignoring unknown LogLevel '{raw}'. Valid: ALL, DEBUG, INFO, WARN, ERROR, FATAL, OFF.");
                return;
            }
            repo.Root.Level = level;
            repo.RaiseConfigurationChanged(System.EventArgs.Empty);
            Log.Info($"Logger: root level overridden to {level.Name} via Config.LogLevel.");
        }
    }
}
