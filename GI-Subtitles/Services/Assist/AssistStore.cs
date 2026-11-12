using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Data;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>One thing Kaption read off a game's screen and still remembers.</summary>
    public sealed record AssistStoredReading(string Account, string ReaderId, string Value, string Screen, DateTimeOffset SeenUtc);

    /// <summary>
    /// How a reader is doing: when it last read anything and how many sessions it has been switched on since. A
    /// game update can move the interface, and then a reader just stops reading: this is where that shows.
    /// </summary>
    public sealed record AssistReaderHealth(string ReaderId, DateTimeOffset? LastSeenUtc, int SessionsSinceSeen);

    /// <summary>
    /// What the in-game helper has read, per game: <c>%APPDATA%\Kaption\&lt;Game&gt;\assist.json</c>. Readings are
    /// filed under the account that was on screen (a local tag, never the UID itself), so two accounts on one PC do
    /// not mix. Writes are atomic and debounced; <see cref="DeleteGame"/> forgets everything read for a game.
    /// Thread-safe. Nothing here is sent anywhere.
    /// </summary>
    public sealed class AssistStore
    {
        public const string FileName = "assist.json";

        /// <summary>The account readings are filed under while no reader has seen a UID.</summary>
        public const string LocalAccount = "local";

        /// <summary>A reader's "last read" moves on disk at most this often while it keeps reading.</summary>
        public static readonly TimeSpan SeenSaveEvery = TimeSpan.FromMinutes(10);

        /// <summary>How long a write that failed waits before it is tried again.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private sealed class StoredReading
        {
            public string Value { get; set; }
            public string Screen { get; set; }
            public DateTimeOffset SeenUtc { get; set; }
        }

        private sealed class StoredHealth
        {
            public DateTimeOffset? LastSeenUtc { get; set; }
            public int SessionsSinceSeen { get; set; }
        }

        private sealed class GameFile
        {
            public int Version { get; set; } = 1;
            /// <summary>Random, made on this PC: what turns a UID into a tag that means nothing anywhere else.</summary>
            public string Salt { get; set; }
            public Dictionary<string, Dictionary<string, StoredReading>> Accounts { get; set; } = new();
            public Dictionary<string, StoredHealth> Health { get; set; } = new();
        }

        private readonly Func<string, string> _fileFor;
        private readonly TimeSpan _debounce;
        private readonly object _lock = new object();
        private readonly Dictionary<string, GameFile> _games = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
        /// <summary>Games whose file is there but could not be read yet: what is in memory is only what happened since.</summary>
        private readonly HashSet<string> _unread = new(StringComparer.Ordinal);
        private Timer _timer;

        /// <summary>Raised after a game's readings changed or were deleted, from the thread that changed them.</summary>
        public event Action<string> Changed;

        /// <summary>Raised after <see cref="DeleteGame"/>: whoever keeps a copy of a reading forgets it too.</summary>
        public event Action<string> Deleted;

        public static AssistStore Default { get; } = new AssistStore(game => Path.Combine(GameDataPaths.GameDir(game), FileName), TimeSpan.FromSeconds(2));

        /// <param name="fileFor">The file of a game (GameId, as the data folder spells it).</param>
        /// <param name="debounce">How long a change waits for more before it is written; zero writes at once.</param>
        public AssistStore(Func<string, string> fileFor, TimeSpan debounce)
        {
            _fileFor = fileFor ?? throw new ArgumentNullException(nameof(fileFor));
            _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
        }

        /// <summary>Remembers a reading; a newer one of the same reader and account replaces it.</summary>
        public void Record(string gameId, string account, string readerId, string value, string screen, DateTimeOffset seenUtc)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(readerId) || value == null) return;
            account = string.IsNullOrEmpty(account) ? LocalAccount : account;
            lock (_lock)
            {
                GameFile file = Load(gameId);
                if (!file.Accounts.TryGetValue(account, out var readings)) file.Accounts[account] = readings = new();
                readings[readerId] = new StoredReading { Value = value, Screen = screen ?? "", SeenUtc = seenUtc.ToUniversalTime() };
                Seen(file, readerId, seenUtc, force: true);
                MarkDirty(gameId);
            }
            Changed?.Invoke(gameId);
        }

        /// <summary>A reader read something (whether or not it is kept): its health clock starts again.</summary>
        public void MarkSeen(string gameId, string readerId, DateTimeOffset at)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(readerId)) return;
            lock (_lock)
            {
                if (Seen(Load(gameId), readerId, at, force: false)) MarkDirty(gameId);
            }
        }

        private static bool Seen(GameFile file, string readerId, DateTimeOffset at, bool force)
        {
            if (!file.Health.TryGetValue(readerId, out StoredHealth health)) file.Health[readerId] = health = new StoredHealth();
            bool worthSaving = force || health.SessionsSinceSeen != 0 || health.LastSeenUtc == null || at - health.LastSeenUtc.Value >= SeenSaveEvery;
            if (!worthSaving) return false;
            health.LastSeenUtc = at.ToUniversalTime();
            health.SessionsSinceSeen = 0;
            return true;
        }

        /// <summary>
        /// The watch starts looking for these readers in this run of the app: one more session each has been on.
        /// Counted once per game per process.
        /// </summary>
        public void SessionStarted(string gameId, IEnumerable<string> readerIds)
        {
            if (string.IsNullOrEmpty(gameId) || readerIds == null) return;
            lock (_lock)
            {
                if (!_sessions.Add(gameId)) return;
                GameFile file = Load(gameId);
                bool any = false;
                foreach (string id in readerIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!file.Health.TryGetValue(id, out StoredHealth health)) file.Health[id] = health = new StoredHealth();
                    health.SessionsSinceSeen++;
                    any = true;
                }
                if (any) MarkDirty(gameId);
            }
        }

        /// <summary>Everything remembered for a game, newest first.</summary>
        public IReadOnlyList<AssistStoredReading> Readings(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return Array.Empty<AssistStoredReading>();
            lock (_lock)
            {
                return Load(gameId).Accounts
                    .SelectMany(a => a.Value.Select(r => new AssistStoredReading(a.Key, r.Key, r.Value.Value, r.Value.Screen, r.Value.SeenUtc)))
                    .OrderByDescending(r => r.SeenUtc)
                    .ToList();
            }
        }

        public IReadOnlyList<AssistReaderHealth> Health(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return Array.Empty<AssistReaderHealth>();
            lock (_lock)
            {
                return Load(gameId).Health.OrderBy(h => h.Key, StringComparer.Ordinal)
                    .Select(h => new AssistReaderHealth(h.Key, h.Value.LastSeenUtc, h.Value.SessionsSinceSeen)).ToList();
            }
        }

        /// <summary>
        /// The tag readings of the account with this UID are filed under: a salted hash cut short. The UID is not
        /// kept, and the tag is not written to any log.
        /// </summary>
        public string AccountTag(string gameId, string uid)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrWhiteSpace(uid)) return LocalAccount;
            lock (_lock)
            {
                GameFile file = Load(gameId);
                if (string.IsNullOrEmpty(file.Salt))
                {
                    file.Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                    MarkDirty(gameId);
                }
                return TagFor(uid, file.Salt);
            }
        }

        internal static string TagFor(string uid, string salt)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt + "|" + uid.Trim()));
            return "a-" + Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
        }

        /// <summary>
        /// Forgets everything read for a game: the readings, the health counters and the file. False when the file
        /// could not be removed (held open, read-only): memory is empty either way, and an empty file is written
        /// over the old one as soon as the disk allows, so the old readings cannot come back in this run.
        /// </summary>
        public bool DeleteGame(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return true;
            bool gone = true;
            lock (_lock)
            {
                _games[gameId] = new GameFile();
                _dirty.Remove(gameId);
                _unread.Remove(gameId);
                try
                {
                    string path = _fileFor(gameId);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    gone = false;
                    Logger.Log.Warn($"In-game helper: the stored readings of {gameId} were not deleted: {ex.Message}");
                    MarkDirty(gameId);
                }
            }
            Deleted?.Invoke(gameId);
            Changed?.Invoke(gameId);
            return gone;
        }

        /// <summary>Writes what is waiting, now (the app is closing, a test wants to read the file). What fails stays waiting.</summary>
        public void Flush()
        {
            lock (_lock)
            {
                foreach (string game in _dirty.ToArray())
                {
                    if (Write(game)) _dirty.Remove(game);
                }
                // A full disk or a held file: try again later instead of losing the reading at the next restart.
                if (_dirty.Count > 0 && _timer != null) _timer.Change(RetryAfter, Timeout.InfiniteTimeSpan);
            }
        }

        private void MarkDirty(string gameId)
        {
            if (_debounce == TimeSpan.Zero)
            {
                if (Write(gameId)) _dirty.Remove(gameId);
                else _dirty.Add(gameId); // the next change or Flush tries again
                return;
            }
            _dirty.Add(gameId);
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }

        private bool Write(string gameId)
        {
            try
            {
                if (_unread.Contains(gameId))
                {
                    // The file was held by something else when it was first asked for. It is read now, and what
                    // happened since is laid over it; while it still cannot be read it is not written over.
                    GameFile onDisk = Read(gameId, out bool stillHeld);
                    if (stillHeld) return false;
                    _unread.Remove(gameId);
                    if (onDisk != null) _games[gameId] = Merge(Clean(onDisk), _games[gameId]);
                }
                string path = _fileFor(gameId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_games[gameId], Json));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Logger.Log.Warn($"In-game helper: the readings of {gameId} were not saved, trying again later: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Tolerant: a file that is missing, cut off or from a later version reads as empty. A file that is there
        /// but held by something else is another matter: memory starts empty, and the file is merged in before the
        /// first write (<see cref="Write"/>), never replaced.
        /// </summary>
        private GameFile Load(string gameId)
        {
            if (_games.TryGetValue(gameId, out GameFile loaded)) return loaded;
            GameFile file = Read(gameId, out bool held);
            if (held)
            {
                _unread.Add(gameId);
                Logger.Log.Warn($"In-game helper: the stored readings of {gameId} cannot be read right now; they are kept and read again before anything is saved.");
            }
            file = Clean(file ?? new GameFile());
            _games[gameId] = file;
            return file;
        }

        /// <summary>The file's content; null when there is none or it is broken. <paramref name="held"/>: it is there and could not be opened.</summary>
        private GameFile Read(string gameId, out bool held)
        {
            held = false;
            try
            {
                string path = _fileFor(gameId);
                if (!File.Exists(path) && !AtomicFile.RecoverInterruptedSave(path)) return null;
                GameFile file = JsonSerializer.Deserialize<GameFile>(File.ReadAllText(path), Json);
                return file != null && file.Version == 1 ? file : null;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
            {
                Logger.Log.Warn($"In-game helper: the stored readings of {gameId} do not read ({ex.GetType().Name}); starting empty.");
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                held = true;
                return null;
            }
        }

        /// <summary>A file someone edited: an entry without its content is dropped, not carried around as a null.</summary>
        private static GameFile Clean(GameFile file)
        {
            file.Accounts ??= new();
            file.Health ??= new();
            foreach (string account in file.Accounts.Where(a => a.Value == null).Select(a => a.Key).ToArray()) file.Accounts.Remove(account);
            foreach (var readings in file.Accounts.Values)
            {
                foreach (string reader in readings.Where(r => r.Value?.Value == null).Select(r => r.Key).ToArray()) readings.Remove(reader);
            }
            foreach (string reader in file.Health.Where(h => h.Value == null).Select(h => h.Key).ToArray()) file.Health.Remove(reader);
            return file;
        }

        /// <summary>What happened in this run laid over what the file held: newer readings win, session counts add up.</summary>
        private static GameFile Merge(GameFile onDisk, GameFile since)
        {
            onDisk.Salt ??= since.Salt;
            foreach (var account in since.Accounts)
            {
                if (!onDisk.Accounts.TryGetValue(account.Key, out var readings)) onDisk.Accounts[account.Key] = readings = new();
                foreach (var reading in account.Value) readings[reading.Key] = reading.Value;
            }
            foreach (var health in since.Health)
            {
                if (!onDisk.Health.TryGetValue(health.Key, out StoredHealth known)) onDisk.Health[health.Key] = known = new StoredHealth();
                if (health.Value.LastSeenUtc != null)
                {
                    known.LastSeenUtc = health.Value.LastSeenUtc;
                    known.SessionsSinceSeen = health.Value.SessionsSinceSeen;
                }
                else
                {
                    known.SessionsSinceSeen += health.Value.SessionsSinceSeen;
                }
            }
            return onDisk;
        }
    }
}
