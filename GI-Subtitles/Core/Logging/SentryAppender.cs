// ─────────────────────────────────────────────────────────────────────────────
//  SentryAppender.cs
//  ---------------------------------------------------------------------------
//  log4net → Sentry/GlitchTip bridge. Every Error/Fatal LoggingEvent the app
//  emits is automatically forwarded to GlitchTip without touching the 140+
//  existing Logger.Log.Error(...) call sites. Drop-in: register once at
//  startup (see Logger.RegisterSentryAppender) and every swallowed-catch
//  that logs at Error level surfaces in the dashboard from then on.
//
//  Why a log4net appender instead of editing every catch:
//    * Most catches do `Logger.Log.Error($"X failed: {ex}")` and never
//      bubble the exception. App.xaml.cs only forwards UNHANDLED exceptions
//      to Sentry, so swallowed catches are invisible to GlitchTip — that's
//      why the ChooseRegion OverflowException never reached the dashboard.
//    * Adding one CrashReportingService.ReportException call per catch is
//      140+ touch-points that future code will inevitably forget. An
//      appender hooks the existing convention exactly once.
//
//  What this appender does NOT do:
//    * Forward Warn/Info/Debug. log4net Error+ is the right gate — Warn in
//      this codebase is reserved for expected non-fatal conditions
//      (unauthenticated session, cleanup failures, missing config keys).
//      Forwarding Warn would drown the dashboard in noise.
//    * Send when the user has opted out. Checks CrashReportingService.IsEnabled
//      on every call and no-ops when consent is false (or the SDK never
//      started). Same privacy contract as the rest of the crash pipeline.
//    * Bypass the BeforeSend hook in CrashReportingService.StartSdk — events
//      flow through SentrySdk.CaptureException/CaptureMessage which still
//      run the BeforeSend scrubber on every payload.
//
//  Quota / abuse protection (GlitchTip free tier is ~1k events/month):
//    * Per-session cap: MaxEventsPerSession events then the appender drops
//      silently. Prevents a runaway OCR-loop exception from burning a
//      month's quota in a few minutes.
//    * Client-side dedup: `(exceptionType + first stack frame)` within a
//      60 s sliding window is suppressed. Sentry server-side fingerprinting
//      groups duplicates anyway, but each event still counts against quota
//      on the wire — this saves the round-trip.
//
//  Recursion guard:
//    * CrashReportingService.ReportException emits a `[CRASH-REPORT:*]` log
//      line so user.app.log retains forensic detail even when the SDK is
//      offline. If this appender forwarded those lines we'd recurse into
//      the SDK indefinitely. Filter messages whose rendered text starts
//      with `[CRASH-REPORT:` and they bypass the appender entirely.
//
//  Failure isolation: every operation is wrapped in try/catch. An appender
//  that throws during Append is catastrophic — log4net would loop trying
//  to log the failure of the log call. Anything we can't handle gets
//  swallowed and forgotten.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Threading;
using GI_Subtitles.Services.Observability;
using log4net.Appender;
using log4net.Core;
using Sentry;

namespace GI_Subtitles.Core.Logging
{
    /// <summary>
    /// log4net appender that forwards Error+ events to the Sentry SDK
    /// (which CrashReportingService points at GlitchTip). Attaches to the
    /// log4net root in <see cref="GI_Subtitles.Common.Logger.RegisterSentryAppender"/>.
    /// </summary>
    public class SentryAppender : AppenderSkeleton
    {
        /// <summary>Hard ceiling on events per process lifetime. Tunable.</summary>
        private const int MaxEventsPerSession = 20;

        /// <summary>Sliding window for client-side dedup of identical events.</summary>
        private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);

        /// <summary>How many recent keys we'll track before sweeping.</summary>
        private const int DedupMapSizeLimit = 128;

        /// <summary>
        /// Marker on log lines we emit ourselves — filtered out to break the
        /// CrashReportingService.ReportException → Logger.Log.Error → appender
        /// → CaptureException recursion.
        /// </summary>
        private const string CrashReportMarker = "[CRASH-REPORT:";

        private static int _eventCount;
        private static readonly Dictionary<string, DateTime> _recentKeys =
            new Dictionary<string, DateTime>();
        private static readonly object _dedupLock = new object();

        protected override void Append(LoggingEvent loggingEvent)
        {
            // Bail as cheaply as possible when we won't forward anyway.
            if (loggingEvent == null) return;
            if (loggingEvent.Level == null) return;
            if (loggingEvent.Level < Level.Error) return;
            if (!CrashReportingService.IsEnabled) return;

            try
            {
                string message = loggingEvent.RenderedMessage ?? string.Empty;

                // Skip lines we wrote ourselves to avoid an infinite recursion
                // (CrashReportingService.ReportException logs at Error level).
                if (message.StartsWith(CrashReportMarker, StringComparison.Ordinal))
                    return;

                Exception ex = loggingEvent.ExceptionObject;

                string dedupKey = BuildDedupKey(ex, message);
                if (!ShouldEmit(dedupKey)) return;

                // Per-session cap: increment first, decrement back if over,
                // so we don't permanently lose budget on a contended check.
                int current = Interlocked.Increment(ref _eventCount);
                if (current > MaxEventsPerSession)
                {
                    Interlocked.Decrement(ref _eventCount);
                    return;
                }

                string loggerName = loggingEvent.LoggerName ?? "log4net";
                string contextTag = "log4net:" + loggerName;

                if (ex != null)
                {
                    SentrySdk.CaptureException(ex, scope =>
                    {
                        scope.SetTag("context", contextTag);
                        scope.SetTag("log_level", loggingEvent.Level.Name);
                        if (!string.IsNullOrEmpty(message))
                            scope.SetExtra("log_message", Truncate(message, 2000));
                    });
                }
                else
                {
                    // No structured exception attached — most call sites format
                    // the exception into the message string instead of using
                    // the 2-arg Error(message, exception) overload. We still
                    // get an event, just with looser grouping (by message hash).
                    SentrySdk.CaptureMessage(
                        Truncate(message, 2000),
                        scope =>
                        {
                            scope.SetTag("context", contextTag);
                            scope.SetTag("log_level", loggingEvent.Level.Name);
                        },
                        SentryLevel.Error);
                }
            }
            catch
            {
                // Never let an appender throw — log4net would loop trying to
                // log the failure. Silent drop is the right answer here.
            }
        }

        /// <summary>
        /// Build a key for client-side dedup. For exceptions we use
        /// <c>type:firstStackFrame</c> so a single bug looping doesn't burn
        /// quota; for message-only events we use the first 200 chars of the
        /// message since that's all we have to identify it by.
        /// </summary>
        private static string BuildDedupKey(Exception ex, string message)
        {
            if (ex != null)
            {
                string firstFrame = ExtractFirstFrame(ex);
                return ex.GetType().FullName + "|" + firstFrame;
            }
            return "msg|" + Truncate(message, 200);
        }

        /// <summary>
        /// Return true when this key hasn't been emitted in the dedup window.
        /// Atomically updates the recent-keys map. Caller holds no locks.
        /// </summary>
        private static bool ShouldEmit(string key)
        {
            DateTime now = DateTime.UtcNow;
            lock (_dedupLock)
            {
                if (_recentKeys.TryGetValue(key, out var ts) && (now - ts) < DedupWindow)
                    return false;
                _recentKeys[key] = now;

                // Sweep when the map gets large. O(N) but N is bounded so
                // it's cheap and runs at most occasionally.
                if (_recentKeys.Count > DedupMapSizeLimit)
                {
                    DateTime cutoff = now - DedupWindow;
                    var toRemove = new List<string>();
                    foreach (var kv in _recentKeys)
                        if (kv.Value < cutoff) toRemove.Add(kv.Key);
                    foreach (var k in toRemove) _recentKeys.Remove(k);
                }
                return true;
            }
        }

        private static string ExtractFirstFrame(Exception ex)
        {
            try
            {
                string st = ex.StackTrace;
                if (string.IsNullOrEmpty(st)) return "(no-stack)";
                int newline = st.IndexOf('\n');
                string firstLine = newline > 0 ? st.Substring(0, newline) : st;
                return firstLine.Trim();
            }
            catch
            {
                return "(unknown-frame)";
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
