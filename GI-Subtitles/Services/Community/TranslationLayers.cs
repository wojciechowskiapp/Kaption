using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GI_Subtitles.Services.QuickTranslate;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    /// <summary>
    /// What a line reads as on this PC: the pack, then accepted community fixes, then the player's own fixes.
    /// Lookups are exact-key and allocation-free against an immutable snapshot, so the subtitle tick and the
    /// Ctrl+Q worker can read from any thread; writes are serialised and reach disk before they are published.
    /// </summary>
    public sealed class TranslationLayers
    {
        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot(new Dictionary<string, string>(), new Dictionary<string, UserFix>());

            public readonly FrozenDictionary<string, string> Community;
            public readonly FrozenDictionary<string, UserFix> User;
            public readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> CommunityBySpan;
            public readonly FrozenDictionary<string, UserFix>.AlternateLookup<ReadOnlySpan<char>> UserBySpan;
            public readonly bool HasCommunitySpan, HasUserSpan;
            public readonly bool IsEmpty;

            public Snapshot(IDictionary<string, string> community, IDictionary<string, UserFix> user)
            {
                Community = community.ToFrozenDictionary(StringComparer.Ordinal);
                User = user.ToFrozenDictionary(StringComparer.Ordinal);
                HasCommunitySpan = Community.TryGetAlternateLookup(out CommunityBySpan);
                HasUserSpan = User.TryGetAlternateLookup(out UserBySpan);
                IsEmpty = Community.Count == 0 && User.Count == 0;
            }
        }

        private readonly object _gate = new object();
        private readonly string _userPath;
        private readonly string _communityPath;
        private volatile Snapshot _snapshot = Snapshot.Empty;
        private Dictionary<string, UserFix> _user = new Dictionary<string, UserFix>(StringComparer.Ordinal);
        private Dictionary<string, CommunityEntry> _community = new Dictionary<string, CommunityEntry>(StringComparer.Ordinal);
        private string _since;
        private volatile bool _userLoaded, _communityLoaded;
        private int _overlayInFlight;
        private long _overlayNextTicks;
        private int _overlayFailures;

        /// <param name="game">Desktop game id, the <c>%APPDATA%\Kaption\&lt;Game&gt;</c> folder name.</param>
        /// <param name="wireGame">Lower-case wire id sent to the server.</param>
        /// <param name="lang">Lower-case target language.</param>
        public TranslationLayers(string game, string wireGame, string lang, string userFile, string communityFile)
        {
            Game = game ?? "";
            WireGame = wireGame ?? "";
            Lang = lang ?? "";
            _userPath = userFile ?? throw new ArgumentNullException(nameof(userFile));
            _communityPath = communityFile ?? throw new ArgumentNullException(nameof(communityFile));
        }

        public string Game { get; }
        public string WireGame { get; }
        public string Lang { get; }
        internal string CommunityFilePath => _communityPath;

        /// <summary>Raised on the writing thread after a change was saved and published.</summary>
        public event EventHandler Changed;

        public bool IsEmpty => _snapshot.IsEmpty;
        public int UserFixCount => _snapshot.User.Count;
        public int CommunityCount => _snapshot.Community.Count;

        /// <summary>Both files have been read (lock-free check; <see cref="EnsureLoaded"/> does the reading).</summary>
        public bool IsLoaded => _userLoaded && _communityLoaded;

        /// <summary>
        /// Claims the overlay fetch for this game and language: never while one runs, and, unless
        /// <paramref name="force"/>, not before the time the last one set. A next time further ahead than
        /// <paramref name="longestWait"/> means the clock went back and is ignored.
        /// </summary>
        internal bool TryBeginOverlayFetch(DateTime nowUtc, bool force, TimeSpan longestWait)
        {
            if (Interlocked.CompareExchange(ref _overlayInFlight, 1, 0) != 0) return false;
            long next = Interlocked.Read(ref _overlayNextTicks);
            bool wait = next != 0 && nowUtc.Ticks < next && next - nowUtc.Ticks <= longestWait.Ticks;
            if (force || !wait) return true;
            Interlocked.Exchange(ref _overlayInFlight, 0);
            return false;
        }

        /// <summary>Ends a claimed fetch; returns how many failures in a row this game and language has had.</summary>
        internal int EndOverlayFetch(DateTime nowUtc, bool failed, TimeSpan untilNext)
        {
            int failures = 0;
            if (failed) failures = Interlocked.Increment(ref _overlayFailures);
            else Interlocked.Exchange(ref _overlayFailures, 0);
            Interlocked.Exchange(ref _overlayNextTicks, (nowUtc + untilNext).Ticks);
            Interlocked.Exchange(ref _overlayInFlight, 0);
            return failures;
        }

        /// <summary>The newest <c>acceptedAt</c> held, sent as <c>since</c> on the next overlay fetch.</summary>
        public string CommunitySince
        {
            get { lock (_gate) return _since; }
        }

        // ── reads (any thread, no allocation) ────────────────────────────

        /// <summary>Pack, then community, then the player's own fix.</summary>
        public string Resolve(string enKey, string packValue)
        {
            var s = _snapshot;
            if (s.IsEmpty || string.IsNullOrEmpty(enKey)) return packValue;
            if (TryUser(s, enKey, out var fix)) return fix.Text;
            return TryCommunity(s, enKey, out var target) ? target : packValue;
        }

        /// <summary>What every player gets for the line: the pack, then community. Ignores the player's own fix.</summary>
        public string ResolveShared(string enKey, string packValue)
        {
            var s = _snapshot;
            if (string.IsNullOrEmpty(enKey)) return packValue;
            return TryCommunity(s, enKey, out var target) ? target : packValue;
        }

        public bool TryGetUserFix(string enKey, out UserFix fix)
        {
            fix = null;
            return !string.IsNullOrEmpty(enKey) && TryUser(_snapshot, enKey, out fix);
        }

        /// <summary>The player's fixes, newest first.</summary>
        public IReadOnlyList<UserFix> UserFixes =>
            _snapshot.User.Values.OrderByDescending(f => f.SavedAt).ThenBy(f => f.En, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Ctrl+Q: the same layers inside the segmenter's probe, so a fix gets the cleanup and number filling the pack's
        /// text gets there. Thread-safe; the probe runs on the Ctrl+Q worker.
        /// </summary>
        public Func<string, MatchProbe> Over(Func<string, MatchProbe> probe)
        {
            if (probe == null) return null;
            return text =>
            {
                var p = probe(text);
                if (string.IsNullOrEmpty(p.Key) || string.IsNullOrEmpty(p.Value)) return p;
                string value = Resolve(p.Key, p.Value);
                return ReferenceEquals(value, p.Value) ? p : new MatchProbe(value, p.Key, p.Ambiguous);
            };
        }

        // Stored keys are trimmed; a key with edge whitespace is retried trimmed, as a span.
        private static bool TryUser(Snapshot s, string key, out UserFix fix)
        {
            if (s.User.Count == 0) { fix = null; return false; }
            if (s.User.TryGetValue(key, out fix)) return true;
            return HasEdgeWhitespace(key) && s.HasUserSpan && s.UserBySpan.TryGetValue(key.AsSpan().Trim(), out fix);
        }

        private static bool TryCommunity(Snapshot s, string key, out string target)
        {
            if (s.Community.Count == 0) { target = null; return false; }
            if (s.Community.TryGetValue(key, out target)) return true;
            return HasEdgeWhitespace(key) && s.HasCommunitySpan && s.CommunityBySpan.TryGetValue(key.AsSpan().Trim(), out target);
        }

        private static bool HasEdgeWhitespace(string key) =>
            key.Length > 0 && (char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[key.Length - 1]));

        // ── loading ──────────────────────────────────────────────────────

        /// <summary>Reads both files once (a file that could not be read is retried on the next call).</summary>
        public void EnsureLoaded()
        {
            bool changed;
            lock (_gate)
            {
                changed = LoadUserLocked() | LoadCommunityLocked();
                if (changed) PublishLocked();
            }
            if (changed) RaiseChanged();
        }

        // True when something new was read into memory.
        private bool LoadUserLocked()
        {
            if (_userLoaded) return false;
            var fixes = LayerFiles.ReadUserFixes(_userPath, out var state);
            if (state == LayerFileState.Unreadable) return false;
            _user = fixes;
            _userLoaded = true;
            return fixes.Count > 0;
        }

        private bool LoadCommunityLocked()
        {
            if (_communityLoaded) return false;
            var entries = LayerFiles.ReadCommunity(_communityPath, out var since, out var state);
            if (state == LayerFileState.Unreadable) return false;
            _community = entries;
            _since = since;
            _communityLoaded = true;
            return entries.Count > 0;
        }

        private void PublishLocked()
        {
            var community = new Dictionary<string, string>(_community.Count, StringComparer.Ordinal);
            foreach (var kv in _community) community[kv.Key] = kv.Value.Target;
            _snapshot = new Snapshot(community, _user);
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Log.Warn($"Translation layers: a Changed listener threw: {ex.Message}"); }
        }

        // ── writes (serialised; disk first, then the snapshot) ───────────

        /// <summary>Saves the player's fix for a line and applies it. Throws <see cref="IOException"/> when it cannot be saved.</summary>
        public void SaveUserFix(UserFix fix)
        {
            if (fix == null || fix.En.Length == 0 || fix.Text.Length == 0)
                throw new ArgumentException("A fix needs an English key and a text.", nameof(fix));
            lock (_gate)
            {
                RequireUserFileLocked();
                var next = new Dictionary<string, UserFix>(_user, StringComparer.Ordinal) { [fix.En] = fix };
                LayerFiles.WriteUserFixes(_userPath, WireGame, Lang, next.Values);
                _user = next;
                PublishLocked();
            }
            RaiseChanged();
        }

        /// <summary>Removes the player's fix; false when the line had none.</summary>
        public bool RemoveUserFix(string enKey)
        {
            string key = (enKey ?? "").Trim();
            if (key.Length == 0) return false;
            lock (_gate)
            {
                RequireUserFileLocked();
                if (!_user.ContainsKey(key)) return false;
                var next = new Dictionary<string, UserFix>(_user, StringComparer.Ordinal);
                next.Remove(key);
                LayerFiles.WriteUserFixes(_userPath, WireGame, Lang, next.Values);
                _user = next;
                PublishLocked();
            }
            RaiseChanged();
            return true;
        }

        /// <summary>Folds entries in as a delta (no server cursor); see <see cref="ApplyOverlay"/>.</summary>
        internal int MergeCommunity(IEnumerable<CommunityEntry> entries) =>
            entries == null ? 0 : ApplyOverlay(new CommunityOverlayPage(entries.ToList(), null, false));

        /// <summary>
        /// Folds a fetched overlay page into the cache, oldest first: a newer entry replaces the line, an empty target
        /// removes it, and a full page replaces the whole cache. The next <c>since</c> is the server's <c>until</c>
        /// (else the newest timestamp seen). Returns how many lines changed. A cache file that cannot be read now is
        /// left alone.
        /// </summary>
        public int ApplyOverlay(CommunityOverlayPage page)
        {
            if (page == null) return 0;
            int changed = 0;
            bool publish;
            lock (_gate)
            {
                bool loadedNow = LoadCommunityLocked();
                if (!_communityLoaded)
                    throw new IOException($"{Path.GetFileName(_communityPath)} could not be read; the cached overlay was left as it is.");
                var next = page.Full
                    ? new Dictionary<string, CommunityEntry>(StringComparer.Ordinal)
                    : new Dictionary<string, CommunityEntry>(_community, StringComparer.Ordinal);
                string since = page.Full ? null : _since;
                foreach (var e in page.Entries.Where(e => e != null && e.En.Length > 0).OrderBy(e => e.AcceptedAtValue))
                {
                    if (e.Target.Length == 0)
                        next.Remove(e.En);
                    else
                        next[e.En] = e;
                    since = CommunityEntry.Later(since, e.AcceptedAt);
                }
                if (page.Until != null) since = page.Until;
                changed = CountChanges(_community, next);
                if (changed > 0 || !string.Equals(since, _since, StringComparison.Ordinal))
                {
                    LayerFiles.WriteCommunity(_communityPath, WireGame, Lang, since, next.Values);
                    _community = next;
                    _since = since;
                }
                publish = changed > 0 || loadedNow;
                if (publish) PublishLocked();
            }
            if (publish) RaiseChanged();
            return changed;
        }

        // Lines added, removed or given another text.
        private static int CountChanges(Dictionary<string, CommunityEntry> before, Dictionary<string, CommunityEntry> after)
        {
            int changes = 0;
            foreach (var kv in after)
                if (!before.TryGetValue(kv.Key, out var old) || !string.Equals(old.Target, kv.Value.Target, StringComparison.Ordinal))
                    changes++;
            foreach (var key in before.Keys)
                if (!after.ContainsKey(key)) changes++;
            return changes;
        }

        private void RequireUserFileLocked()
        {
            if (LoadUserLocked()) PublishLocked();
            if (!_userLoaded)
                throw new IOException($"{Path.GetFileName(_userPath)} could not be read, so it was not overwritten.");
        }
    }
}
