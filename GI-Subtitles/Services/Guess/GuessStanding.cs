using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>A player next to the caller on a board, exactly as the public board shows them.</summary>
    public sealed record GuessNeighbour(int Rank, string Name, string Tag, bool Hidden, int Points, int Gap);

    /// <summary>A place on a board at a moment (server time), with the player's points then.</summary>
    public sealed record GuessPlace(int Rank, int Points, DateTimeOffset At);

    /// <summary>The caller's place on one monthly board.</summary>
    public sealed record GuessBoardStanding(int Rank, int Points, int Players, GuessNeighbour Above, GuessNeighbour Below)
    {
        /// <summary>
        /// The place right after the player's last result that scored on this board (the server's <c>lastScored</c>,
        /// sent when the app asks with <c>since</c>). Their points have not moved since, so a lower place now is a pass.
        /// </summary>
        public GuessPlace LastScored { get; init; }

        /// <summary>
        /// How many players on this board have strictly more points than the player now (the server's <c>ahead</c>).
        /// Null from a Worker that does not send it; the neighbour above then says it only when they have more points.
        /// </summary>
        public int? Ahead { get; init; }
    }

    /// <summary>
    /// GET /api/app/guess/standing (design section 5): the caller's place on the global board and on each game's board
    /// this month. Parsed tolerantly: a missing or odd field becomes null or 0, never an exception, and a board the
    /// player is not on is null.
    /// </summary>
    public sealed class GuessStanding
    {
        public const string GlobalBoard = "global";

        /// <summary>The board month, "2026-11" (UTC on the server).</summary>
        public string Period { get; init; }
        public DateTimeOffset? ClosesAt { get; init; }
        public GuessBoardStanding Global { get; init; }
        /// <summary>Board id (manifest wire id, "genshin") → standing, or null when the player has no place there.</summary>
        public IReadOnlyDictionary<string, GuessBoardStanding> Games { get; init; } = new Dictionary<string, GuessBoardStanding>();
        public int TodayPlayed { get; init; }
        public int TodayOf { get; init; }

        /// <summary>The server's clock when it answered; baselines are stamped with it, never with the PC's clock.</summary>
        public DateTimeOffset? At { get; init; }

        /// <summary>The player finished a ranked puzzle this month, with or without a place: the normal read cadence.</summary>
        public bool PlayedThisMonth { get; init; }

        /// <summary>The server's read cadence (<c>poll</c>): with something played this month, and with nothing. Null when not sent.</summary>
        public TimeSpan? PollEvery { get; init; }
        public TimeSpan? PollIdle { get; init; }

        /// <summary>The standing on a board id ("global" or a game's wire id); null when none.</summary>
        public GuessBoardStanding Board(string board)
        {
            if (string.IsNullOrEmpty(board)) return null;
            if (string.Equals(board, GlobalBoard, StringComparison.OrdinalIgnoreCase)) return Global;
            return Games.TryGetValue(board.ToLowerInvariant(), out var s) ? s : null;
        }

        /// <summary>Null when the text is not JSON or not an object with a period.</summary>
        public static GuessStanding Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return Parse(doc.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Null for anything that is not a standing object (an empty body arrives as Undefined).</summary>
        public static GuessStanding Parse(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            string period = Str(root, "period");
            if (string.IsNullOrEmpty(period)) return null;

            var games = new Dictionary<string, GuessBoardStanding>(StringComparer.Ordinal);
            bool played = root.TryGetProperty("global", out var globalEl) && Played(globalEl);
            if (root.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in g.EnumerateObject())
                {
                    string id = p.Name.Trim().ToLowerInvariant();
                    if (id.Length == 0 || id == GlobalBoard) continue;
                    games[id] = ParseBoard(p.Value);
                    played |= Played(p.Value);
                }
            }
            // The review's earlier draft: "game": { "id": "genshin", "standing": { ... } }.
            else if (root.TryGetProperty("game", out var one) && one.ValueKind == JsonValueKind.Object
                     && Str(one, "id") is string oneId && oneId.Length > 0 && one.TryGetProperty("standing", out var st))
            {
                games[oneId.Trim().ToLowerInvariant()] = ParseBoard(st);
            }

            int todayPlayed = 0, of = 0;
            if (root.TryGetProperty("today", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                todayPlayed = Int(t, "played");
                of = Int(t, "of");
            }

            TimeSpan? every = null, idle = null;
            if (root.TryGetProperty("poll", out var poll) && poll.ValueKind == JsonValueKind.Object)
            {
                every = Int(poll, "minutes") is int m and > 0 ? TimeSpan.FromMinutes(m) : null;
                idle = Int(poll, "idleMinutes") is int i and > 0 ? TimeSpan.FromMinutes(i) : null;
            }

            return new GuessStanding
            {
                Period = period,
                ClosesAt = Time(Str(root, "closesAt")),
                Global = root.TryGetProperty("global", out var gl) ? ParseBoard(gl) : null,
                Games = games,
                TodayPlayed = todayPlayed,
                TodayOf = of,
                At = Time(Str(root, "at")),
                PlayedThisMonth = played || todayPlayed > 0,
                PollEvery = every,
                PollIdle = idle,
            };
        }

        private static bool Played(JsonElement board) => board.ValueKind == JsonValueKind.Object && Int(board, "played") > 0;

        private static DateTimeOffset? Time(string s) =>
            s != null && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

        private static GuessBoardStanding ParseBoard(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            int rank = Int(e, "rank");
            if (rank < 1) return null; // "no place yet" (rank null) is no standing for the notice
            return new GuessBoardStanding(rank, Int(e, "points"), Int(e, "players"),
                e.TryGetProperty("above", out var a) ? ParseNeighbour(a) : null,
                e.TryGetProperty("below", out var b) ? ParseNeighbour(b) : null)
            {
                LastScored = e.TryGetProperty("lastScored", out var ls) ? ParsePlace(ls) : null,
                Ahead = ParseAhead(e, rank),
            };
        }

        /// <summary>The count of players with more points; null when missing, not a whole number, or more than the places above.</summary>
        private static int? ParseAhead(JsonElement e, int rank) =>
            e.TryGetProperty("ahead", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n >= 0 && n < rank
                ? n : null;

        /// <summary>Null unless it has a place, the points it had there (checked against the standing's) and a time.</summary>
        private static GuessPlace ParsePlace(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            int rank = Int(e, "rank"), points = Int(e, "points");
            return rank >= 1 && points >= 1 && Time(Str(e, "at")) is DateTimeOffset at ? new GuessPlace(rank, points, at) : null;
        }

        private static GuessNeighbour ParseNeighbour(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            bool hidden = e.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
            return new GuessNeighbour(Int(e, "rank"), hidden ? null : Str(e, "name"), hidden ? null : Str(e, "tag"),
                hidden, Int(e, "points"), Int(e, "gap"));
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt32(out int i)) return i;
                if (v.TryGetDouble(out double d) && !double.IsNaN(d)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
            }
            return 0;
        }
    }
}
