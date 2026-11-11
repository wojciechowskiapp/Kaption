using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>
    /// The place last accepted on one board: what a later drop is measured from. <see cref="At"/> is the server's time
    /// of that place (the read that saw it, or the player's last score); null in baselines written by 2.5.7 and 2.5.8.
    /// </summary>
    public sealed record GuessBaseline(string Period, int Rank, int Points, DateTimeOffset? At = null);

    /// <summary>Per-board baselines, kept in Config as one small JSON object ({"global":{"period":"2026-11","rank":3,"points":140,"at":"…"}}).</summary>
    public sealed class GuessBaselines
    {
        public static readonly GuessBaselines Empty = new(new Dictionary<string, GuessBaseline>());

        private readonly IReadOnlyDictionary<string, GuessBaseline> _boards;

        public GuessBaselines(IReadOnlyDictionary<string, GuessBaseline> boards) =>
            _boards = boards ?? new Dictionary<string, GuessBaseline>();

        public GuessBaseline this[string board] =>
            board != null && _boards.TryGetValue(board.ToLowerInvariant(), out var b) ? b : null;

        public IEnumerable<string> Boards => _boards.Keys;

        /// <summary>A copy with <paramref name="board"/> set; a null value removes it.</summary>
        public GuessBaselines With(string board, GuessBaseline value)
        {
            var copy = _boards.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (value == null) copy.Remove(board.ToLowerInvariant());
            else copy[board.ToLowerInvariant()] = value;
            return new GuessBaselines(copy);
        }

        public string ToJson()
        {
            var o = new JsonObject();
            foreach (var (board, b) in _boards.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var e = new JsonObject { ["period"] = b.Period, ["rank"] = b.Rank, ["points"] = b.Points };
                if (b.At is DateTimeOffset at) e["at"] = GuessOvertakeRule.Iso(at);
                o[board] = e;
            }
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
                    DateTimeOffset? at = e.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String
                                         && DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed : null;
                    if (string.IsNullOrEmpty(period) || rank < 1) continue;
                    boards[p.Name.ToLowerInvariant()] = new GuessBaseline(period, rank, points, at);
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

    /// <summary>What one watched board's read came to.</summary>
    public enum GuessCheckResult
    {
        /// <summary>No place on this board this month.</summary>
        NoPlace,
        /// <summary>The first place known this month (nothing stored, no last score from the server): kept as the start.</summary>
        First,
        /// <summary>The place has no points yet.</summary>
        NoPoints,
        /// <summary>Lower than the reference, and more players have more points now than were above then: a pass.</summary>
        Passed,
        /// <summary>
        /// Lower than the reference with no proof that anyone passed on points (tie-breaks, as far as the standing
        /// shows): no notice, and the reference stays, so a pass that shows later is measured from it.
        /// </summary>
        TieBreak,
        /// <summary>Fewer points than the reference had (a result of the player's own was voided): re-based, no notice.</summary>
        OwnPointsFell,
        /// <summary>The same place or better: re-based.</summary>
        NothingNew,
        /// <summary>
        /// The server's place after the last score has other points than the standing: the standing is a stale copy
        /// (or the server older than its guard). Skipped for this read; the stored place stays as it was.
        /// </summary>
        StaleCopy,
    }

    /// <summary>One watched board: its place now, what it was measured from (null for none) and where that came from.</summary>
    public sealed record GuessBoardCheck(string Board, GuessBoardStanding Current, GuessBaseline Reference, bool FromLastScore, GuessCheckResult Result);

    /// <summary>
    /// When the player has been passed (R4 section 2.4). Pure: the baselines in, the baselines to keep and at most one
    /// candidate out. A board's baseline moves to the current place unless that board is lower than its reference, so a
    /// notice that waits (capped for the day, the game in front) reports the whole drop when it finally shows, and a
    /// drop not yet proven to be a pass is not forgotten.
    /// <para>
    /// The reference is the newer of two places: the one the app stored (stamped with the server's time of the read
    /// that saw it) and the server's <see cref="GuessBoardStanding.LastScored"/>, the place right after the player's last
    /// scoring result. The second is what makes a pass count when the app never saw the place it beat: on 2026-11-09
    /// the owner was #1 after his last score, was passed twice while Kaption was closed, and the app's first read saw #3.
    /// </para>
    /// </summary>
    public static class GuessOvertakeRule
    {
        public sealed record Outcome(GuessOvertake Candidate, GuessBaselines Baselines)
        {
            /// <summary>Every watched board's result, for the read's log line.</summary>
            public IReadOnlyList<GuessBoardCheck> Checks { get; init; } = Array.Empty<GuessBoardCheck>();
        }

        /// <summary>The boards watched: the selected game's board (when it has one) and the global board.</summary>
        public static IReadOnlyList<string> Watched(string gameBoard) =>
            string.IsNullOrWhiteSpace(gameBoard) || string.Equals(gameBoard, GuessStanding.GlobalBoard, StringComparison.OrdinalIgnoreCase)
                ? new[] { GuessStanding.GlobalBoard }
                : new[] { gameBoard.Trim().ToLowerInvariant(), GuessStanding.GlobalBoard };

        /// <param name="lastShownDay">
        /// The local day the last notice reached the player (<see cref="GuessNotifySettings.LastShownDay"/>). A place
        /// stored without a time this month next to it was likely written by that delivery (2.5.7, 2.5.8), so it stays
        /// the reference against a last score from that day or before with no more points than it: the same drop is
        /// never told twice, and a pass after a newer (higher) score still counts.
        /// </param>
        /// <param name="localDay">The local day of a server time, as <paramref name="lastShownDay"/> counts days.</param>
        public static Outcome Evaluate(GuessBaselines baselines, GuessStanding standing, string gameBoard, string lastShownDay = null,
                                       Func<DateTimeOffset, string> localDay = null)
        {
            baselines ??= GuessBaselines.Empty;
            if (standing == null) return new Outcome(null, baselines);
            localDay ??= t => GuessNotifySettings.Today(t.LocalDateTime);
            GuessOvertake candidate = null;
            var checks = new List<GuessBoardCheck>();
            foreach (string board in Watched(gameBoard))
            {
                GuessBoardStanding current = standing.Board(board);
                if (current == null)
                {
                    checks.Add(new GuessBoardCheck(board, null, baselines[board], false, GuessCheckResult.NoPlace));
                    continue; // no place this month: keep what we had
                }
                GuessBaseline stored = baselines[board];
                if (stored != null && !string.Equals(stored.Period, standing.Period, StringComparison.Ordinal)) stored = null; // a new month
                GuessBaseline reference = stored;
                bool fromLastScore = false;
                if (IsStaleCopy(current))
                {
                    // Re-based on it, the stale place would carry this read's time: the next `since` would then be
                    // newer than the score, the server would not replay it, and a pass after it would be measured from
                    // the stale place and lost. The board is left alone until a read whose two places agree.
                    checks.Add(new GuessBoardCheck(board, current, stored, false, GuessCheckResult.StaleCopy));
                    continue;
                }
                if (UsesLastScore(current, stored, standing.Period, lastShownDay, localDay) is GuessPlace last)
                {
                    // The player scored after the app last looked (or the app never stamped what it saw): their place
                    // right after that score is the newer reference. Kept as the baseline, so the next read can send a
                    // later `since` and the server skips the replay.
                    reference = new GuessBaseline(standing.Period, last.Rank, last.Points, last.At);
                    baselines = baselines.With(board, reference);
                    fromLastScore = true;
                }
                var now = new GuessBaseline(standing.Period, current.Rank, current.Points, standing.At);
                GuessCheckResult result;
                if (reference == null) result = GuessCheckResult.First;
                else if (current.Points <= 0) result = GuessCheckResult.NoPoints;
                else if (current.Points < reference.Points) result = GuessCheckResult.OwnPointsFell;
                else if (current.Rank > reference.Rank) result = PassedOnPoints(current, reference.Rank) ? GuessCheckResult.Passed : GuessCheckResult.TieBreak;
                else result = GuessCheckResult.NothingNew;
                checks.Add(new GuessBoardCheck(board, current, reference, fromLastScore, result));
                if (result == GuessCheckResult.Passed)
                {
                    // The game board comes first in Watched, so it wins when both dropped.
                    candidate ??= new GuessOvertake(board, reference.Rank, current.Rank, current.Above);
                    continue;
                }
                // Re-based on the lower place, the tied player would count as someone who was always above, and a
                // pass on points behind them could never be proven (2026-11-10: #1 to #5, three passes, no notice).
                if (result == GuessCheckResult.TieBreak)
                {
                    // A kept place from 2.5.7 or 2.5.8 has no time: it gets this read's, or every read would send
                    // "since 0" and the server would replay the board again.
                    if (reference.At == null && standing.At is DateTimeOffset readAt)
                        baselines = baselines.With(board, reference with { At = readAt });
                    continue;
                }
                baselines = baselines.With(board, now); // first read, new month, no points yet, own points fell, same or better
            }
            return new Outcome(candidate, baselines) { Checks = checks };
        }

        /// <summary>The server's place after the last score when it is the reference for this read, else null.</summary>
        private static GuessPlace UsesLastScore(GuessBoardStanding current, GuessBaseline stored, string period, string lastShownDay,
                                                Func<DateTimeOffset, string> localDay)
        {
            if (current.LastScored is not GuessPlace last) return null;
            if (stored == null) return last;
            if (stored.At is DateTimeOffset seen) return last.At > seen ? last : null;
            // Stored by 2.5.7 or 2.5.8, without a time. More points after the last score than in that place prove a
            // score after it (points rise only by scoring; past IsStaleCopy they equal the standing's).
            if (last.Points > stored.Points) return last;
            // Same points: with a notice delivered this month it is likely the delivered place. It stays, unless the
            // last score came on a later day than that notice.
            return ToldThisMonth(lastShownDay, period, localDay) && string.CompareOrdinal(localDay(last.At), lastShownDay) <= 0 ? null : last;
        }

        /// <summary>
        /// The standing's place and the server's place after the last score disagree on the points: the standing is a
        /// colo's copy from before that score, so the two are not the same moment (a climb could read as a pass).
        /// </summary>
        public static bool IsStaleCopy(GuessBoardStanding current) =>
            current?.LastScored is GuessPlace last && last.Points != current.Points;

        /// <summary>
        /// The last notice's local day falls in this (UTC) month: on or after the local day the month began. A plain
        /// "2026-11-" prefix would miss a notice on the local 1st before UTC midnight (east of UTC) or on the local last
        /// day after it (west), and the same drop could be told again.
        /// </summary>
        private static bool ToldThisMonth(string lastShownDay, string period, Func<DateTimeOffset, string> localDay)
        {
            if (string.IsNullOrEmpty(lastShownDay) || string.IsNullOrEmpty(period)) return false;
            if (!DateTimeOffset.TryParseExact(period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                              DateTimeStyles.AssumeUniversal, out var start))
                return lastShownDay.StartsWith(period + "-", StringComparison.Ordinal);
            return string.CompareOrdinal(lastShownDay, localDay(start)) >= 0;
        }

        /// <summary>
        /// How many players have strictly more points than the player now, as far as the standing proves it: the
        /// server's count, else every place above when the player just above has more points (the board is sorted by
        /// points). Null behind a neighbour with equal points and no count: who is ahead on points is not known.
        /// </summary>
        public static int? AheadOnPoints(GuessBoardStanding current)
        {
            if (current == null) return null;
            if (current.Ahead is int ahead) return ahead;
            return current.Above != null && current.Above.Points > current.Points ? current.Rank - 1 : null;
        }

        /// <summary>
        /// Someone passed the player on points since the place <paramref name="referenceRank"/>: only referenceRank - 1
        /// players were above then, so referenceRank or more with strictly more points now include one who was not.
        /// A drop at equal points is a tie-break the player's own solve can cause; it backs no "someone passed you".
        /// </summary>
        public static bool PassedOnPoints(GuessBoardStanding current, int referenceRank) =>
            AheadOnPoints(current) is int ahead && ahead >= referenceRank;

        /// <summary>
        /// A notice reached the player (or the player opened the ranking): every watched board, and the notice's own
        /// board when the selected game changed since, starts again from now (stamped with the read's server time).
        /// </summary>
        public static GuessBaselines Delivered(GuessBaselines baselines, GuessStanding standing, string gameBoard, string noticeBoard = null)
        {
            baselines ??= GuessBaselines.Empty;
            if (standing == null) return baselines;
            var boards = Watched(gameBoard).ToList();
            if (!string.IsNullOrWhiteSpace(noticeBoard) && !boards.Contains(noticeBoard.Trim().ToLowerInvariant()))
                boards.Add(noticeBoard.Trim().ToLowerInvariant());
            foreach (string board in boards)
                if (standing.Board(board) is GuessBoardStanding s && !IsStaleCopy(s)) // a stale copy keeps its stored place, as in Evaluate
                    baselines = baselines.With(board, new GuessBaseline(standing.Period, s.Rank, s.Points, standing.At));
            return baselines;
        }

        /// <summary>
        /// The standing request's <c>since</c>, per watched board (<c>genshin:&lt;time&gt;,global:&lt;time&gt;</c>): each
        /// board's stored server time, so the server sends <c>lastScored</c> only for a board scored on after it. "0" for
        /// a board with nothing stored or a place without a time (2.5.7, 2.5.8): that board is replayed once, which also
        /// covers a game the player has just selected.
        /// </summary>
        public static string Since(GuessBaselines baselines, string gameBoard) =>
            string.Join(",", Watched(gameBoard).Select(b => b + ":" + (baselines?[b]?.At is DateTimeOffset at ? Iso(at) : "0")));

        /// <summary>The server's time format: UTC, milliseconds, "Z".</summary>
        public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>
        /// The read's one Info line: what each watched board came to, the notice it found (if any) and when the next read
        /// is. Enough to answer "why did no notice come?" from app.log. Names no other player.
        /// </summary>
        public static string Describe(GuessStanding standing, Outcome outcome, TimeSpan nextRead)
        {
            var sb = new StringBuilder("Guess standing");
            if (standing == null) return sb.Append(": no standing in the answer; next read in ").Append(nextRead).Append('.').ToString();
            sb.Append(" (").Append(standing.Period).Append("): ");
            var checks = outcome?.Checks ?? Array.Empty<GuessBoardCheck>();
            if (checks.All(c => c.Result == GuessCheckResult.NoPlace))
                sb.Append(standing.PlayedThisMonth ? "no place this month yet (played)" : "nothing played this month");
            else
                sb.Append(string.Join("; ", checks.Select(DescribeCheck)));
            sb.Append(outcome?.Candidate is GuessOvertake c ? $"; notice: {c.Board}" : "; no notice");
            return sb.Append("; next read in ").Append(nextRead).Append('.').ToString();
        }

        private static string DescribeCheck(GuessBoardCheck c)
        {
            if (c.Current == null) return $"{c.Board} no place";
            string head = $"{c.Board} #{c.Current.Rank} ({c.Current.Points} pts)";
            string from = c.Reference == null ? null
                : c.FromLastScore ? $"#{c.Reference.Rank} after the last score ({Short(c.Reference.At)})"
                : $"#{c.Reference.Rank} at the last read";
            return c.Result switch
            {
                GuessCheckResult.First => head + " first place seen, kept as the start",
                GuessCheckResult.NoPoints => head + " no points yet",
                GuessCheckResult.Passed => $"{head} passed: {from} → #{c.Current.Rank}",
                GuessCheckResult.TieBreak => AheadOnPoints(c.Current) is int ahead
                    ? $"{head} lower than {from} on tie-breaks only ({ahead} with more points), the reference kept"
                    : $"{head} lower than {from} behind a player with the same points; who has more is not known, the reference kept",
                GuessCheckResult.OwnPointsFell => $"{head} own points fell since {from} ({c.Reference.Points} → {c.Current.Points}, a void), re-based",
                GuessCheckResult.StaleCopy => $"{head} a stale copy (the last score has {c.Current.LastScored?.Points} pts), skipped, the stored place kept",
                _ => $"{head} nothing new (was {from})",
            };
        }

        private static string Short(DateTimeOffset? t) =>
            t is DateTimeOffset v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "unknown time";
    }
}
