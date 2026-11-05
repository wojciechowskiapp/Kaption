using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>The place last accepted on one board: what a later drop is measured from.</summary>
    public sealed record GuessBaseline(string Period, int Rank, int Points);

    /// <summary>Per-board baselines, kept in Config as one small JSON object ({"global":{"period":"2026-11","rank":3,"points":140}}).</summary>
    public sealed class GuessBaselines
    {
        public static readonly GuessBaselines Empty = new(new Dictionary<string, GuessBaseline>());

        private readonly IReadOnlyDictionary<string, GuessBaseline> _boards;

        public GuessBaselines(IReadOnlyDictionary<string, GuessBaseline> boards) =>
            _boards = boards ?? new Dictionary<string, GuessBaseline>();

        public GuessBaseline this[string board] =>
            board != null && _boards.TryGetValue(board.ToLowerInvariant(), out var b) ? b : null;

        public IEnumerable<string> Boards => _boards.Keys;

        public GuessBaselines With(string board, GuessBaseline value)
        {
            var copy = _boards.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            copy[board.ToLowerInvariant()] = value;
            return new GuessBaselines(copy);
        }

        public string ToJson()
        {
            var o = new JsonObject();
            foreach (var (board, b) in _boards.OrderBy(p => p.Key, StringComparer.Ordinal))
                o[board] = new JsonObject { ["period"] = b.Period, ["rank"] = b.Rank, ["points"] = b.Points };
            return o.ToJsonString();
        }

        /// <summary>Empty for null, broken JSON or entries without a period and a rank: an edited file only resets the baselines.</summary>
        public static GuessBaselines Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
                var boards = new Dictionary<string, GuessBaseline>(StringComparer.Ordinal);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var e = p.Value;
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string period = e.TryGetProperty("period", out var pe) && pe.ValueKind == JsonValueKind.String ? pe.GetString() : null;
                    int rank = e.TryGetProperty("rank", out var r) && r.TryGetInt32(out int ri) ? ri : 0;
                    int points = e.TryGetProperty("points", out var pt) && pt.TryGetInt32(out int pi) ? pi : 0;
                    if (string.IsNullOrEmpty(period) || rank < 1) continue;
                    boards[p.Name.ToLowerInvariant()] = new GuessBaseline(period, rank, points);
                }
                return new GuessBaselines(boards);
            }
            catch (JsonException)
            {
                return Empty;
            }
        }
    }

    /// <summary>Someone passed the player on <see cref="Board"/>: the place went from <see cref="From"/> to <see cref="To"/>.</summary>
    public sealed record GuessOvertake(string Board, int From, int To, GuessNeighbour Above)
    {
        public bool IsGlobal => string.Equals(Board, GuessStanding.GlobalBoard, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the player has been passed (R4 section 2.4). Pure: the baselines in, the baselines to keep and at most one
    /// candidate out. A board's baseline moves to the current place unless that board has a drop to report, so a notice
    /// that waits (capped for the day, the game in front) reports the whole drop when it finally shows.
    /// </summary>
    public static class GuessOvertakeRule
    {
        public sealed record Outcome(GuessOvertake Candidate, GuessBaselines Baselines);

        /// <summary>The boards watched: the selected game's board (when it has one) and the global board.</summary>
        public static IReadOnlyList<string> Watched(string gameBoard) =>
            string.IsNullOrWhiteSpace(gameBoard) || string.Equals(gameBoard, GuessStanding.GlobalBoard, StringComparison.OrdinalIgnoreCase)
                ? new[] { GuessStanding.GlobalBoard }
                : new[] { gameBoard.Trim().ToLowerInvariant(), GuessStanding.GlobalBoard };

        public static Outcome Evaluate(GuessBaselines baselines, GuessStanding standing, string gameBoard)
        {
            baselines ??= GuessBaselines.Empty;
            if (standing == null) return new Outcome(null, baselines);
            GuessOvertake candidate = null;
            foreach (string board in Watched(gameBoard))
            {
                GuessBoardStanding current = standing.Board(board);
                if (current == null) continue; // no place this month: keep what we had
                GuessBaseline before = baselines[board];
                var now = new GuessBaseline(standing.Period, current.Rank, current.Points);
                bool dropped = before != null
                               && string.Equals(before.Period, standing.Period, StringComparison.Ordinal)
                               && current.Points > 0
                               && current.Rank > before.Rank
                               && PassedOnPoints(current);
                if (dropped)
                {
                    // The game board comes first in Watched, so it wins when both dropped.
                    candidate ??= new GuessOvertake(board, before.Rank, current.Rank, current.Above);
                    continue;
                }
                baselines = baselines.With(board, now); // first read, new month, no points yet, same or better, or a tie-break
            }
            return new Outcome(candidate, baselines);
        }

        /// <summary>
        /// The player just above has more points. A drop at equal points is a tie-break the player's own solve can
        /// cause (more attempts, a slower time), so "someone passed you" would be a claim the board does not back.
        /// </summary>
        public static bool PassedOnPoints(GuessBoardStanding current) =>
            current?.Above != null && current.Above.Points > current.Points;

        /// <summary>
        /// A notice reached the player (or the player opened the ranking): every watched board, and the notice's own
        /// board when the selected game changed since, starts again from now.
        /// </summary>
        public static GuessBaselines Delivered(GuessBaselines baselines, GuessStanding standing, string gameBoard, string noticeBoard = null)
        {
            baselines ??= GuessBaselines.Empty;
            if (standing == null) return baselines;
            var boards = Watched(gameBoard).ToList();
            if (!string.IsNullOrWhiteSpace(noticeBoard) && !boards.Contains(noticeBoard.Trim().ToLowerInvariant()))
                boards.Add(noticeBoard.Trim().ToLowerInvariant());
            foreach (string board in boards)
                if (standing.Board(board) is GuessBoardStanding s)
                    baselines = baselines.With(board, new GuessBaseline(standing.Period, s.Rank, s.Points));
            return baselines;
        }
    }
}
