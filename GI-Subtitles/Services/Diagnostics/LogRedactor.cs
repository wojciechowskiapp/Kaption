// ─────────────────────────────────────────────────────────────────────────────
//  LogRedactor.cs
//  ---------------------------------------------------------------------------
//  Single chokepoint for stripping personal data and secrets out of anything
//  that goes into a diagnostic bundle.
//
//  Everything a user sends us passes through here. Redaction spread across
//  several call sites drifts, and the drift is only ever discovered after
//  something has already leaked, so every artifact — log files, config dumps,
//  environment reports, the free-text note — goes through Scrub().
//
//  What gets removed, and why:
//    - Windows usernames. `C:\Users\<name>\` appears in most log lines and is
//      very often the person's real name.
//    - Bearer/session tokens. A device session JWT is a live credential; one
//      pasted into a Discord thread is an account takeover.
//    - Secret-shaped config values (tokens, keys, DSNs, passwords).
//    - Email local parts. The domain is kept because it identifies the OAuth
//      provider and is genuinely useful for triage; the local part is not.
//
//  What is deliberately NOT removed: game dialogue, file paths below the user
//  directory, versions, hardware details. Those are the diagnostic payload.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Diagnostics
{
    /// <summary>
    /// Removes personal data and credentials from text destined for a
    /// diagnostic bundle. Pure functions, safe to call from any thread.
    /// </summary>
    public static class LogRedactor
    {
        public const string RedactedToken = "[REDACTED_TOKEN]";
        public const string RedactedValue = "[REDACTED]";

        private const RegexOptions Opts =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        /// <summary>
        /// Backstop against a pathological line. Redaction runs over whatever a
        /// user's log happens to contain, which is not a controlled input.
        /// </summary>
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        // Three base64url segments — the shape of a JWT. Matched before the
        // generic key/value rule so tokens are caught even in prose log lines
        // ("refreshing session eyJhbGci...").
        //
        // The trailing segments are deliberately allowed to be short. Real
        // signatures are long, but an unsigned or truncated token is still a
        // credential-shaped string, and the cost of the two error directions is
        // wildly asymmetric: a false positive redacts a harmless string, a false
        // negative publishes a live session token. The `eyJ` anchor (base64 for
        // `{"`) is a strong enough signal to carry the looser tail.
        private static readonly Regex JwtPattern = new Regex(
            @"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{2,}\.[A-Za-z0-9_-]{2,}", Opts, MatchTimeout);

        // JSON or key=value pairs whose NAME suggests a secret. Captures the
        // key and the punctuation so we can rebuild the line with the value
        // replaced, keeping the file parseable.
        private static readonly Regex SecretAssignment = new Regex(
            @"(""?[A-Za-z0-9_.\-]*(?:token|secret|password|passwd|apikey|api_key|dsn|cookie|auth|credential|fingerprint)[A-Za-z0-9_.\-]*""?)\s*([:=])\s*(""[^""]*""|'[^']*'|[^\s,;}\]]+)",
            Opts, MatchTimeout);

        private static readonly Regex EmailPattern = new Regex(
            @"\b([A-Za-z0-9._%+\-]+)@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b", Opts, MatchTimeout);

        // Matches the user directory on any drive, plus the UNC form, so a
        // path copied from another machine is scrubbed too.
        private static readonly Regex UserPathPattern = new Regex(
            @"([A-Za-z]:\\Users\\|\\\\[^\\]+\\Users\\|/home/|/Users/)([^\\/\r\n""':;,)]+)", Opts, MatchTimeout);

        /// <summary>
        /// Applies every redaction rule. Null-safe; returns null for null input
        /// so callers can pass optional fields straight through.
        ///
        /// Works a line at a time. Running the patterns over a whole file as one
        /// string overflows the regex engine's stack on multi-megabyte input —
        /// and screenshot_log.txt has no size cap, so that input is reachable.
        /// </summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            int start = 0;
            while (start <= text.Length)
            {
                int end = text.IndexOf('\n', start);
                bool last = end < 0;
                int lineEnd = last ? text.Length : end;

                sb.Append(ScrubLine(text.Substring(start, lineEnd - start)));
                if (last) break;

                sb.Append('\n');
                start = end + 1;
            }
            return sb.ToString();
        }

        private static string ScrubLine(string line)
        {
            if (line.Length == 0) return line;

            try
            {
                line = JwtPattern.Replace(line, RedactedToken);
                line = SecretAssignment.Replace(line, m =>
                {
                    string quote = m.Groups[3].Value.StartsWith("\"", StringComparison.Ordinal) ? "\"" : "";
                    return m.Groups[1].Value + m.Groups[2].Value + " " + quote + RedactedValue + quote;
                });
                line = EmailPattern.Replace(line, m => MaskEmail(m.Groups[1].Value, m.Groups[2].Value));
                line = UserPathPattern.Replace(line, m => m.Groups[1].Value + "<user>");
                return line;
            }
            catch (RegexMatchTimeoutException)
            {
                // Never emit a line we could not fully check.
                return "[REDACTION_TIMED_OUT]";
            }
        }

        /// <summary>
        /// Keeps the first character and the domain: <c>michal@gmail.com</c>
        /// becomes <c>m***@gmail.com</c>. Enough to tell two accounts apart in
        /// a support thread without publishing the address.
        /// </summary>
        private static string MaskEmail(string localPart, string domain)
        {
            if (string.IsNullOrEmpty(localPart)) return "***@" + domain;
            string head = localPart.Substring(0, 1);
            return head + "***@" + domain;
        }

        /// <summary>
        /// Shortens an identifier to a correlatable prefix. Used for machine
        /// fingerprints, which are a key-derivation input for the encrypted
        /// dictionary files and must never be sent in full — a prefix is still
        /// enough to tell whether two bundles came from the same machine.
        /// </summary>
        public static string ShortId(string id, int length = 8)
        {
            if (string.IsNullOrWhiteSpace(id)) return "(none)";
            id = id.Trim();
            return id.Length <= length ? id : id.Substring(0, length) + "…";
        }
    }
}
