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

        /// <summary>
        /// True when debug records would actually be recorded somewhere.
        /// <para>Guard every <c>Log.Debug($"...")</c> with this. log4net
        /// checks the level itself, but only AFTER the caller has already
        /// built the interpolated string — the level check cannot save an
        /// allocation that happened before log4net was entered. On a
        /// per-frame path that is a string built and thrown away on every
        /// tick, for output nobody will ever read.</para>
        /// <para>Cheap to call: one volatile read of the logger level.</para>
        /// </summary>
        public static bool IsDebugEnabled => Log.IsDebugEnabled;

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
        /// Name of the rolling file appender declared in app.config. Its
        /// threshold — not the root level — is what decides how verbose
        /// <c>app.log</c> is. See <see cref="ApplyLevel"/>.
        /// </summary>
        private const string FileAppenderName = "LogFileAppender";

        /// <summary>
        /// Shipped default for the on-disk log. The root level sits at DEBUG
        /// so the in-memory ring buffer captures detail for diagnostic
        /// bundles; the file stays at INFO so log volume is unchanged.
        /// </summary>
        private static readonly log4net.Core.Level DefaultFileThreshold = log4net.Core.Level.Info;

        /// <summary>
        /// Runtime override for how verbose logging is. Shipped default is
        /// DEBUG to memory / INFO to disk (see app.config). Users who need
        /// debug-level logs on disk for troubleshooting can set the
        /// <c>LogLevel</c> key in Config.json to DEBUG (or TRACE / WARN /
        /// ERROR / ALL / OFF), or use the Diagnostics section in Settings,
        /// which calls <see cref="SetDetailedLogging"/>. Unrecognised values
        /// are logged and ignored, leaving the config-file default in force.
        ///
        /// Called once at startup from App.xaml.cs after the in-memory
        /// appender is attached, and again whenever log4net re-reads its
        /// configuration — <c>XmlConfigurator(Watch = true)</c> in
        /// MainWindow.xaml.cs means an edit to app.config would otherwise
        /// silently revert a runtime override.
        /// </summary>
        public static void ApplyConfiguredLevel()
        {
            string raw = Config.Get<string>("LogLevel", null);
            var repo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();

            if (string.IsNullOrWhiteSpace(raw))
            {
                // No override: root stays wherever app.config put it (DEBUG, so
                // the ring buffer stays useful) and the file drops back to INFO.
                ApplyLevel(repo, repo.Root.Level, DefaultFileThreshold);
                return;
            }

            var level = repo.LevelMap[raw.Trim()];
            if (level == null)
            {
                Log.Warn($"Logger: ignoring unknown LogLevel '{raw}'. Valid: ALL, DEBUG, INFO, WARN, ERROR, FATAL, OFF.");
                return;
            }

            // An explicit LogLevel governs both sinks: asking for DEBUG means
            // "put debug in the file", and asking for WARN means "quieter
            // everywhere", not "quieter on disk but still verbose in memory".
            ApplyLevel(repo, level, level);
            Log.Info($"Logger: level overridden to {level.Name} via Config.LogLevel.");
        }

        /// <summary>
        /// Turns detailed (debug-level) on-disk logging on or off and persists
        /// the choice, taking effect immediately — log4net reads the level on
        /// every call, so no restart is required.
        ///
        /// A restart is still worth offering afterwards, because the override
        /// is not applied until well into <c>App.OnStartup</c>; everything
        /// logged before that (engine init, dictionary load, license checks)
        /// is recorded at the shipped default. That early window is where most
        /// "it starts but nothing happens" reports originate.
        /// </summary>
        public static void SetDetailedLogging(bool enabled)
        {
            Config.Set("LogLevel", enabled ? "DEBUG" : "");
            ApplyConfiguredLevel();
        }

        /// <summary>True when on-disk logging is currently at DEBUG or finer.</summary>
        public static bool IsDetailedLoggingEnabled()
        {
            var repo = (log4net.Repository.Hierarchy.Hierarchy)log4net.LogManager.GetRepository();
            var appender = FindFileAppender(repo);
            var threshold = appender?.Threshold ?? repo.Root.Level;
            return threshold != null && threshold <= log4net.Core.Level.Debug;
        }

        private static void ApplyLevel(
            log4net.Repository.Hierarchy.Hierarchy repo,
            log4net.Core.Level rootLevel,
            log4net.Core.Level fileThreshold)
        {
            // The root must never sit above the file threshold: log4net checks
            // the logger level first, so a root of INFO would drop debug records
            // before the appender ever sees them.
            repo.Root.Level = rootLevel != null && rootLevel <= fileThreshold
                ? rootLevel
                : fileThreshold;

            var appender = FindFileAppender(repo);
            if (appender != null) appender.Threshold = fileThreshold;

            repo.RaiseConfigurationChanged(System.EventArgs.Empty);
        }

        private static log4net.Appender.AppenderSkeleton FindFileAppender(
            log4net.Repository.Hierarchy.Hierarchy repo)
        {
            foreach (var appender in repo.Root.Appenders)
            {
                if (appender is log4net.Appender.RollingFileAppender rolling &&
                    string.Equals(rolling.Name, FileAppenderName, System.StringComparison.Ordinal))
                {
                    return rolling;
                }
            }
            return null;
        }
    }
}
