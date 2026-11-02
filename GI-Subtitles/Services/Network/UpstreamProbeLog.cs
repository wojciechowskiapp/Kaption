using System;
using System.Net.Http;

namespace GI_Subtitles.Services.Network
{
    /// <summary>
    /// One-line, stack-free log text for best-effort upstream probes. Their failures are
    /// network weather, so callers log it at Warn, which never reaches GlitchTip.
    /// </summary>
    public static class UpstreamProbeLog
    {
        /// <summary>"Upstream repo date check failed for &lt;host&gt;: &lt;reason&gt;".</summary>
        public static string ProbeFailed(string url, Exception ex, TimeSpan timeout)
            => $"Upstream repo date check failed for {Host(url)}: {Describe(ex, timeout)}";

        /// <summary>
        /// Exception type and message on one line, plus the HTTP status when known. With
        /// <paramref name="timeout"/> set, a cancellation is reported as that timeout.
        /// </summary>
        public static string Describe(Exception ex, TimeSpan? timeout = null)
        {
            if (ex == null) return "unknown error";
            string type = ex.GetType().Name;
            // HttpClient.Timeout's own exception wraps a TimeoutException and names its duration.
            if (ex is OperationCanceledException && timeout.HasValue && ex.InnerException is not TimeoutException)
                return $"{type}: timed out after {timeout.Value.TotalSeconds:0} s";
            string message = (ex.Message ?? string.Empty).ReplaceLineEndings(" ");
            if (ex is HttpRequestException http && http.StatusCode.HasValue)
                return $"{type} (HTTP {(int)http.StatusCode.Value}): {message}";
            return $"{type}: {message}";
        }

        /// <summary>The URL's host only: no path, query or credentials.</summary>
        public static string Host(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : "(unknown host)";
    }
}
