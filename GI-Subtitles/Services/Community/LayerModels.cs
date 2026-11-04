using System;
using System.Globalization;

namespace GI_Subtitles.Services.Community
{
    /// <summary>The player's own version of one line, kept in <c>overrides.&lt;lang&gt;.json</c>.</summary>
    public sealed class UserFix
    {
        public UserFix(string en, string text, string was, string speaker, string note, DateTimeOffset savedAt)
        {
            En = (en ?? "").Trim();
            Text = text ?? "";
            Was = was ?? "";
            Speaker = speaker ?? "";
            Note = note ?? "";
            SavedAt = savedAt;
        }

        /// <summary>The English dictionary key, trimmed.</summary>
        public string En { get; }
        public string Text { get; }
        /// <summary>The shared translation (pack, then community) when the fix was saved.</summary>
        public string Was { get; }
        public string Speaker { get; }
        public string Note { get; }
        public DateTimeOffset SavedAt { get; }
    }

    /// <summary>
    /// One <c>GET /api/community/overlay</c> answer: <c>items</c> and <c>removed</c> (as entries with an empty target),
    /// the server's <c>until</c> to send as the next <c>since</c>, and whether it is the full set rather than a delta.
    /// </summary>
    public sealed class CommunityOverlayPage
    {
        public static readonly CommunityOverlayPage Empty = new CommunityOverlayPage(Array.Empty<CommunityEntry>(), null, false);

        public CommunityOverlayPage(System.Collections.Generic.IReadOnlyList<CommunityEntry> entries, string until, bool full)
        {
            Entries = entries ?? Array.Empty<CommunityEntry>();
            Until = string.IsNullOrWhiteSpace(until) ? null : until;
            Full = full;
        }

        public System.Collections.Generic.IReadOnlyList<CommunityEntry> Entries { get; }
        public string Until { get; }
        public bool Full { get; }
    }

    /// <summary>One accepted community suggestion from <c>GET /api/community/overlay</c>.</summary>
    public sealed class CommunityEntry
    {
        public CommunityEntry(string en, string target, string acceptedAt)
        {
            En = (en ?? "").Trim();
            Target = target ?? "";
            AcceptedAt = acceptedAt ?? "";
        }

        public string En { get; }
        /// <summary>Empty means the line left the overlay; the pack applies again.</summary>
        public string Target { get; }
        /// <summary>The server's timestamp, kept verbatim so it can be sent back as <c>since</c>.</summary>
        public string AcceptedAt { get; }

        internal DateTimeOffset AcceptedAtValue => ParseTime(AcceptedAt) ?? DateTimeOffset.MinValue;

        internal static DateTimeOffset? ParseTime(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t
                : (DateTimeOffset?)null;

        /// <summary>The later of two server timestamps; an unparsable one never wins.</summary>
        internal static string Later(string current, string candidate)
        {
            var c = ParseTime(candidate);
            if (c == null) return current;
            var cur = ParseTime(current);
            return cur == null || c.Value > cur.Value ? candidate : current;
        }
    }
}
