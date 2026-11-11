using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Notices
{
    /// <summary>A pack whose installed version changed in one sync: downloaded, or relabelled without a download.</summary>
    public sealed record PackVersionChange(string Game, string Language, string FromVersion, string ToVersion);

    /// <summary>The game version inside a pack version: "7.2.0-r10" is game 7.2, patch 0, revision 10.</summary>
    public readonly record struct GameVersion(int Major, int Minor) : IComparable<GameVersion>
    {
        public int CompareTo(GameVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

        /// <summary>"7.2", the way players name a patch.</summary>
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

        /// <summary>
        /// Reads "7.2", "7.2.0", "7.2.0-r10" or "3.7.1". The third number and the revision are hotfixes of the same
        /// game version, so they are read past, not compared.
        /// </summary>
        public static bool TryParse(string packVersion, out GameVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(packVersion)) return false;
            string[] parts = packVersion.Trim().Split('-')[0].Split('.');
            if (parts.Length < 2 || parts.Length > 3) return false;
            if (!IsNumber(parts[0], out int major) || !IsNumber(parts[1], out int minor)) return false;
            if (parts.Length == 3 && !IsNumber(parts[2], out _)) return false;
            version = new GameVersion(major, minor);
            return true;
        }

        private static bool IsNumber(string s, out int n) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }

    /// <summary>One "the new story is in Polish" notice: a game (wire id) and the game version that arrived.</summary>
    public sealed record NewStoryNotice(string Game, GameVersion Version, DateTimeOffset RaisedAt)
    {
        /// <summary>"genshin/7.2": what the once-only memory holds.</summary>
        public string Id => NewStoryRules.IdOf(Game, Version);

        /// <summary>The version's page on the site, "genshin-7-2" (backend <c>slugOf</c> for a version's first pack).</summary>
        public string PageSlug => string.Create(CultureInfo.InvariantCulture, $"{Game.ToLowerInvariant()}-{Version.Major}-{Version.Minor}");
    }

    /// <summary>The rules, without storage: is this a new game version, and how a notice is written down.</summary>
    public static class NewStoryRules
    {
        /// <summary>The notice says "in Polish", so only a Polish pack raises it.</summary>
        public const string NoticeLanguage = "pl";

        /// <summary>A line nobody closed leaves Start after this many days, like the What's new card.</summary>
        public const int PendingDays = 14;

        /// <summary>
        /// How far ahead of the clock a pending line's time may be and still count: a clock that was set back a little
        /// keeps its line, an entry dated far in the future (a hand edit, a clock that was wrong) is ignored.
        /// </summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromDays(1);

        /// <summary>True while a pending line is shown: raised at most <see cref="PendingDays"/> ago, and not in the future.</summary>
        public static bool IsFresh(NewStoryNotice notice, DateTimeOffset now)
        {
            if (notice == null) return false;
            TimeSpan age = now - notice.RaisedAt;
            return age < TimeSpan.FromDays(PendingDays) && age > -ClockSkew;
        }

        public static string IdOf(string game, GameVersion version) => $"{(game ?? string.Empty).Trim().ToLowerInvariant()}/{version}";

        /// <summary>
        /// The game version a sync brought in, when it is news: the pack replaced an installed one (never a first
        /// install) and its game version is higher (7.1 to 7.2). A revision, a third-number hotfix, the same version
        /// again, a downgrade and anything unreadable are not.
        /// </summary>
        public static bool IsNewGameVersion(PackVersionChange change, out GameVersion arrived)
        {
            arrived = default;
            if (change == null || string.IsNullOrWhiteSpace(change.Game)) return false;
            if (!string.Equals(change.Language?.Trim(), NoticeLanguage, StringComparison.OrdinalIgnoreCase)) return false;
            if (!GameVersion.TryParse(change.FromVersion, out var before)) return false;
            if (!GameVersion.TryParse(change.ToVersion, out var after)) return false;
            if (after.CompareTo(before) <= 0) return false;
            arrived = after;
            return true;
        }

        /// <summary>"genshin/7.2@1793891896": a pending line and when it was raised.</summary>
        public static string Encode(NewStoryNotice n) =>
            string.Create(CultureInfo.InvariantCulture, $"{n.Id}@{n.RaisedAt.ToUnixTimeSeconds()}");

        public static bool TryDecode(string entry, out NewStoryNotice notice)
        {
            notice = null;
            if (string.IsNullOrWhiteSpace(entry)) return false;
            int at = entry.LastIndexOf('@');
            int slash = entry.IndexOf('/');
            if (slash <= 0 || at <= slash + 1) return false;
            string game = entry.Substring(0, slash).Trim().ToLowerInvariant();
            if (game.Length == 0 || !GameVersion.TryParse(entry.Substring(slash + 1, at - slash - 1), out var version)) return false;
            if (!long.TryParse(entry.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long unix)) return false;
            DateTimeOffset raised;
            try { raised = DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { return false; }
            notice = new NewStoryNotice(game, version, raised);
            return true;
        }
    }

    /// <summary>
    /// A per-game high-water mark, written as "genshin/7.2": the highest game version a list has seen for each game.
    /// One entry per game, so it never grows and never forgets: a version at or below the mark stays covered whatever
    /// happens to the installed pack later (a rollback and a second upgrade included).
    /// </summary>
    public static class NewStoryMarks
    {
        /// <summary>game → highest version; broken entries are skipped, a repeated game keeps its highest.</summary>
        public static IReadOnlyDictionary<string, GameVersion> Read(IEnumerable<string> entries)
        {
            var marks = new Dictionary<string, GameVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                int slash = entry.IndexOf('/');
                if (slash <= 0) continue;
                string game = entry.Substring(0, slash).Trim().ToLowerInvariant();
                if (game.Length == 0 || !GameVersion.TryParse(entry.Substring(slash + 1), out var version)) continue;
                if (!marks.TryGetValue(game, out var have) || version.CompareTo(have) > 0) marks[game] = version;
            }
            return marks;
        }

        /// <summary>True when <paramref name="version"/> is at or below the game's mark.</summary>
        public static bool Covers(IEnumerable<string> entries, string game, GameVersion version) =>
            !string.IsNullOrWhiteSpace(game) && Read(entries).TryGetValue(game.Trim(), out var mark) && version.CompareTo(mark) <= 0;

        /// <summary>The list with the game's mark raised to <paramref name="version"/> (never lowered), one entry per game.</summary>
        public static IReadOnlyList<string> With(IEnumerable<string> entries, string game, GameVersion version)
        {
            var marks = new Dictionary<string, GameVersion>(Read(entries), StringComparer.OrdinalIgnoreCase);
            string key = (game ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length > 0 && (!marks.TryGetValue(key, out var have) || version.CompareTo(have) > 0)) marks[key] = version;
            return marks.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => NewStoryRules.IdOf(m.Key, m.Value)).ToList();
        }
    }

    /// <summary>
    /// The balloons still to show, in the order they were raised. One is shown at a time (Windows shows one), and the
    /// others keep waiting: showing one never drops another.
    /// </summary>
    public sealed class NewStoryBalloonQueue
    {
        private readonly List<NewStoryNotice> _waiting = new();

        public int Count => _waiting.Count;

        /// <summary>Adds notices not already waiting.</summary>
        public void Add(IEnumerable<NewStoryNotice> notices)
        {
            foreach (var n in notices ?? Array.Empty<NewStoryNotice>())
                if (n != null && !_waiting.Any(w => string.Equals(w.Id, n.Id, StringComparison.OrdinalIgnoreCase))) _waiting.Add(n);
        }

        /// <summary>Drops the notices that are no longer pending (closed on Start, switched off, expired).</summary>
        public void KeepOnly(Func<NewStoryNotice, bool> stillPending) => _waiting.RemoveAll(n => !stillPending(n));

        /// <summary>The next balloon to show, removed from the queue; null when none waits.</summary>
        public NewStoryNotice TakeNext()
        {
            if (_waiting.Count == 0) return null;
            var next = _waiting[0];
            _waiting.RemoveAt(0);
            return next;
        }
    }

    /// <summary>Where the notices' state lives; Config in the app, a dictionary in tests.</summary>
    public interface INewStoryStore
    {
        bool Enabled { get; }
        IReadOnlyList<string> Read(string key);
        void Write(string key, IReadOnlyList<string> values);
    }

    public sealed class ConfigNewStoryStore : INewStoryStore
    {
        public bool Enabled => Config.Get(NewStoryNotices.EnabledKey, true);
        public IReadOnlyList<string> Read(string key) => Config.Get<string[]>(key, null) ?? Array.Empty<string>();
        public void Write(string key, IReadOnlyList<string> values) => Config.Set(key, values.ToArray());
    }

    /// <summary>
    /// One notice per game and game version, ever: a sync offers its version changes, a game version above the game's
    /// mark raises a notice (a tray balloon and a line on Start), and that version or a lower one never raises another.
    /// Off with <see cref="EnabledKey"/>; a closed line stays closed.
    /// </summary>
    public sealed class NewStoryNotices
    {
        public const string EnabledKey = "NewStory.Enabled";
        /// <summary>Written by Kaption: per game, the highest game version already announced ("genshin/7.2").</summary>
        public const string NoticedKey = "NewStory.Noticed";
        /// <summary>Written by Kaption: per game, the highest game version whose notice the player closed or opened.</summary>
        public const string AcknowledgedKey = "NewStory.Acknowledged";
        /// <summary>Written by Kaption: lines still on Start, "game/version@unix".</summary>
        public const string PendingKey = "NewStory.Pending";

        private readonly INewStoryStore _store;
        private readonly object _gate = new();
        private readonly List<NewStoryNotice> _undelivered = new();

        public NewStoryNotices(INewStoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        /// <summary>The app's instance, on Config.</summary>
        public static NewStoryNotices Default { get; } = new(new ConfigNewStoryStore());

        /// <summary>A notice was raised; may fire on a background thread (the pack sync's).</summary>
        public event Action Raised;

        /// <summary>The Start line appeared, was closed, or the switch moved; may fire on any thread.</summary>
        public event Action Changed;

        public bool Enabled => _store.Enabled;

        /// <summary>
        /// Offers one sync's version changes. Returns the notices raised (usually none). With the switch off nothing is
        /// raised, and the version still counts as noticed, so switching it on later brings back nothing old.
        /// </summary>
        public IReadOnlyList<NewStoryNotice> Offer(IEnumerable<PackVersionChange> changes, DateTimeOffset now)
        {
            var raised = new List<NewStoryNotice>();
            if (changes == null) return raised;
            lock (_gate)
            {
                foreach (var change in changes)
                {
                    if (!NewStoryRules.IsNewGameVersion(change, out var version)) continue;
                    var notice = new NewStoryNotice(change.Game.Trim().ToLowerInvariant(), version, now);
                    var noticed = _store.Read(NoticedKey);
                    if (NewStoryMarks.Covers(noticed, notice.Game, version)) continue;
                    bool enabled = _store.Enabled;
                    if (enabled)
                    {
                        // The line first, the mark second: each write is saved on its own, and the sync will not offer
                        // this step again. Stopped between the two, the line is already safe; the other order would
                        // leave a mark without a line, and the notice would be lost for good.
                        // One line per game: a newer version replaces the older one's line.
                        var pending = Decoded(_store.Read(PendingKey)).Where(p => !SameGame(p, notice)).Append(notice);
                        _store.Write(PendingKey, pending.Select(NewStoryRules.Encode).ToList());
                    }
                    _store.Write(NoticedKey, NewStoryMarks.With(noticed, notice.Game, version));
                    if (!enabled) continue;
                    _undelivered.Add(notice);
                    raised.Add(notice);
                }
            }
            if (raised.Count > 0)
            {
                Notify(Raised);
                Notify(Changed);
            }
            return raised;
        }

        /// <summary>Notices raised in this process that no balloon has shown yet; each is handed out once.</summary>
        public IReadOnlyList<NewStoryNotice> TakeUndelivered()
        {
            lock (_gate)
            {
                var list = _undelivered.ToList();
                _undelivered.Clear();
                return list;
            }
        }

        /// <summary>The line Start shows now: the newest one still pending and fresh; null with the switch off.</summary>
        public NewStoryNotice Current(DateTimeOffset now)
        {
            if (!_store.Enabled) return null;
            lock (_gate)
            {
                return Shown(now).OrderByDescending(p => p.RaisedAt).FirstOrDefault();
            }
        }

        /// <summary>True while <paramref name="notice"/> still has its line: switch on, not closed, not expired.</summary>
        public bool IsPending(NewStoryNotice notice, DateTimeOffset now)
        {
            if (notice == null || !_store.Enabled) return false;
            lock (_gate)
            {
                return Shown(now).Any(p => string.Equals(p.Id, notice.Id, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Closes a line for good: the player closed it or opened its page. The version is remembered as acknowledged,
        /// so nothing else repeats that news later (the What's new card of the same article, whenever its feed loads).
        /// </summary>
        public void Dismiss(NewStoryNotice notice)
        {
            if (notice == null) return;
            lock (_gate)
            {
                // The acknowledgement first: each write is saved on its own, and an acknowledged line is no longer shown
                // (Shown), so stopped between the two the line is closed all the same and nothing repeats its news.
                _store.Write(AcknowledgedKey, NewStoryMarks.With(_store.Read(AcknowledgedKey), notice.Game, notice.Version));
                var kept = Decoded(_store.Read(PendingKey)).Where(p => !string.Equals(p.Id, notice.Id, StringComparison.OrdinalIgnoreCase));
                _store.Write(PendingKey, kept.Select(NewStoryRules.Encode).ToList());
                _undelivered.RemoveAll(p => string.Equals(p.Id, notice.Id, StringComparison.OrdinalIgnoreCase));
            }
            Notify(Changed);
        }

        /// <summary>
        /// True when the player already closed or opened the notice for this release note's game version, so its
        /// What's new card would repeat it. Only a version's own article counts; a note about fixes is different news.
        /// Switching the notices off acknowledges nothing.
        /// </summary>
        public bool Acknowledges(string noteGame, string noteGameVersion, string noteKind)
        {
            if (!string.Equals(noteKind, NewStoryText.NewVersionKind, StringComparison.OrdinalIgnoreCase)) return false;
            if (!GameVersion.TryParse(noteGameVersion, out var version)) return false;
            lock (_gate) return NewStoryMarks.Covers(_store.Read(AcknowledgedKey), noteGame, version);
        }

        /// <summary>
        /// The switch moved (the setting itself is written by its owner). Off ends what is pending too: the lines on
        /// Start and any waiting balloon are gone for good, so switching back on shows nothing old.
        /// </summary>
        public void OnEnabledChanged()
        {
            if (!_store.Enabled)
            {
                lock (_gate)
                {
                    _undelivered.Clear();
                    if (_store.Read(PendingKey).Count > 0) _store.Write(PendingKey, Array.Empty<string>());
                }
            }
            Notify(Changed);
        }

        /// <summary>The lines on Start now: pending, fresh, and not already closed or opened. Call under the lock.</summary>
        private List<NewStoryNotice> Shown(DateTimeOffset now)
        {
            var acknowledged = _store.Read(AcknowledgedKey);
            return Decoded(_store.Read(PendingKey))
                .Where(p => NewStoryRules.IsFresh(p, now) && !NewStoryMarks.Covers(acknowledged, p.Game, p.Version))
                .ToList();
        }

        private static bool SameGame(NewStoryNotice a, NewStoryNotice b) => string.Equals(a.Game, b.Game, StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<NewStoryNotice> Decoded(IEnumerable<string> entries)
        {
            foreach (string entry in entries ?? Array.Empty<string>())
                if (NewStoryRules.TryDecode(entry, out var n)) yield return n;
        }

        private static void Notify(Action handlers)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                // A listener's failure must not fail the sync that raised the notice.
                try { handler(); }
                catch (Exception ex) { Logger.Log.Warn($"New story notice: a listener failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>The notice's words and link, from the localized formats; a broken format falls back instead of throwing.</summary>
    public static class NewStoryText
    {
        /// <summary>The release-note kind of a game version's own article (the feed's <c>kind</c>).</summary>
        public const string NewVersionKind = "new_version";

        /// <summary>The link opens only a page of the site, whatever a hand-edited format says.</summary>
        public const string SiteOrigin = "https://kaption.one/";

        /// <summary>"Genshin Impact 7.2: the new story is in Polish now" (format: {0} game, {1} version).</summary>
        public static string Line(string format, string gameName, GameVersion version)
        {
            try { return string.Format(CultureInfo.InvariantCulture, format ?? "{0} {1}", gameName, version); }
            catch (FormatException) { return $"{gameName} {version}"; }
        }

        /// <summary>The version's page from the release-note URL format ({0} = slug); null when it is not on the site.</summary>
        public static string PageUrl(string urlFormat, NewStoryNotice notice)
        {
            if (notice == null || string.IsNullOrWhiteSpace(urlFormat)) return null;
            string url;
            try { url = string.Format(CultureInfo.InvariantCulture, urlFormat, notice.PageSlug); }
            catch (FormatException) { return null; }
            return url.StartsWith(SiteOrigin, StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, "kaption.one", StringComparison.OrdinalIgnoreCase) ? url : null;
        }

        /// <summary>
        /// True when the What's new card would repeat the notice: the newest release note is the same game version's
        /// own article. A note about fixes for that version is different news and stays.
        /// </summary>
        public static bool CoversReleaseNote(NewStoryNotice notice, string noteGame, string noteGameVersion, string noteKind) =>
            notice != null
            && string.Equals(noteKind, NewVersionKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(noteGame?.Trim(), notice.Game, StringComparison.OrdinalIgnoreCase)
            && GameVersion.TryParse(noteGameVersion, out var v) && v == notice.Version;
    }
}
