using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GI_Subtitles.Services.Codes
{
    /// <summary>One reward of a code, as the codes worker read it ("Primogem", 60). Names stay in English.</summary>
    public sealed record CodeReward(string Name, long Count);

    /// <summary>An active code from <c>GET /api/app/codes?game=</c>.</summary>
    public sealed record RedeemCode(string Code, IReadOnlyList<CodeReward> Rewards, DateTimeOffset? FirstSeen, DateTimeOffset? ExpiresAt);

    /// <summary>The body of <c>/api/app/codes?game=</c> was not the shape backend/src/lib/codes.ts writes.</summary>
    public sealed class CodesFormatException : Exception
    {
        public CodesFormatException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// One game's public code list (backend/src/lib/codes.ts <c>PublicCodes</c>): <c>{game, checkedAt, active, expired,
    /// program}</c>. <see cref="CheckedAt"/> null means the codes worker never looked at this game, so an empty list
    /// says nothing about whether codes exist. Active codes keep the server's order, newest first.
    /// <see cref="ListedSince"/> is when the game's list began (<c>listedSince</c>, absent from an older server): a code
    /// first seen then was out before the list, so the card says "on the list since", not "added".
    /// </summary>
    public sealed class RedeemCodeList
    {
        /// <summary>backend lib/codes.ts CODE_RE, after its upper-casing.</summary>
        private static readonly Regex CodeShape = new Regex("^[A-Z0-9]{4,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        /// <summary>The server's caps (lib/codes.ts MAX_CODES, MAX_REWARDS): anything past them is not a list it wrote.</summary>
        public const int MaxCodes = 200, MaxRewards = 20;

        public string Game { get; }
        public DateTimeOffset? CheckedAt { get; }
        public IReadOnlyList<RedeemCode> Active { get; }
        /// <summary>Codes that expired in the last 30 days (the server's window); only counted, never shown here.</summary>
        public int ExpiredCount { get; }
        public DateTimeOffset? ListedSince { get; }

        public RedeemCodeList(string game, DateTimeOffset? checkedAt, IReadOnlyList<RedeemCode> active, int expiredCount = 0,
                              DateTimeOffset? listedSince = null)
        {
            Game = game;
            CheckedAt = checkedAt;
            Active = active ?? Array.Empty<RedeemCode>();
            ExpiredCount = expiredCount;
            ListedSince = listedSince;
        }

        /// <summary>Whether the codes worker has checked this game at least once.</summary>
        public bool Checked => CheckedAt.HasValue;

        /// <summary>Active codes whose known expiry is still ahead of <paramref name="now"/>, in the server's order.</summary>
        public IReadOnlyList<RedeemCode> ActiveAt(DateTimeOffset now) =>
            Active.Where(c => c.ExpiresAt == null || c.ExpiresAt > now).ToList();

        /// <summary>
        /// Parses the endpoint's body for <paramref name="expectedGame"/>. A body that is not that object (or names another
        /// game) throws <see cref="CodesFormatException"/>; a single entry the card could not show (a malformed code, a
        /// reward without a name) is skipped, like the site does.
        /// </summary>
        public static RedeemCodeList Parse(string json, string expectedGame)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new CodesFormatException("empty body");
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CodesFormatException("not JSON", ex);
            }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new CodesFormatException("not an object");
                string game = Str(root, "game");
                if (string.IsNullOrEmpty(game)) throw new CodesFormatException("no game");
                if (expectedGame != null && !string.Equals(game, expectedGame, StringComparison.Ordinal))
                    throw new CodesFormatException($"answer is for {game}, not {expectedGame}");

                DateTimeOffset? checkedAt = null;
                if (root.TryGetProperty("checkedAt", out var ca) && ca.ValueKind != JsonValueKind.Null)
                    checkedAt = Time(ca) ?? throw new CodesFormatException("checkedAt is not a time");

                if (!root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.Array)
                    throw new CodesFormatException("no active list");
                if (active.GetArrayLength() > MaxCodes) throw new CodesFormatException("too many codes");

                var codes = new List<RedeemCode>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in active.EnumerateArray())
                {
                    RedeemCode code = ParseCode(item);
                    if (code != null && seen.Add(code.Code)) codes.Add(code);
                }
                int expired = root.TryGetProperty("expired", out var ex2) && ex2.ValueKind == JsonValueKind.Array ? ex2.GetArrayLength() : 0;
                // Optional and never fatal: a server that does not send it, or sends something else, just has no list start.
                DateTimeOffset? listedSince = root.TryGetProperty("listedSince", out var ls) ? Time(ls) : null;
                return new RedeemCodeList(game, checkedAt, codes, expired, listedSince);
            }
        }

        private static RedeemCode ParseCode(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            string code = Str(item, "code")?.Trim().ToUpperInvariant();
            if (code == null || !CodeShape.IsMatch(code)) return null;
            var rewards = new List<CodeReward>();
            if (item.TryGetProperty("rewards", out var rw) && rw.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rw.EnumerateArray().Take(MaxRewards))
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    string name = Str(r, "name")?.Trim();
                    if (string.IsNullOrEmpty(name) || name.Any(char.IsControl)) continue;
                    long count = r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out long n) ? n : 0;
                    if (count < 1) continue;
                    rewards.Add(new CodeReward(name, count));
                }
            }
            DateTimeOffset? firstSeen = item.TryGetProperty("firstSeen", out var fs) ? Time(fs) : null;
            DateTimeOffset? expiresAt = item.TryGetProperty("expiresAt", out var ea) ? Time(ea) : null;
            return new RedeemCode(code, rewards, firstSeen, expiresAt);
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static DateTimeOffset? Time(JsonElement e) =>
            e.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                ? t : null;
    }
}
