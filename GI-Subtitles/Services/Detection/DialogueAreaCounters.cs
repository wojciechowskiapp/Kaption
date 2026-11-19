using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Data;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>What the first session after setup came to, written once into <c>area.json</c>.</summary>
    public sealed record FirstRunReport(
        string Area, string Mode, string Shape, bool MeasuredShape, bool PickerOpened, int Placements, int MatchedLines,
        string FirstLine, bool SessionCheckRaised);

    /// <summary>
    /// Local numbers about the dialogue area, per game: <c>%APPDATA%\Kaption\&lt;Game&gt;\area.json</c> (design 2.8 and
    /// 2.9). Counts and buckets only: no text, no coordinates, no window title, and nothing leaves the PC. The owner
    /// reads it in a diagnostic bundle a player sends, or on a clean profile. Writes are debounced and atomic;
    /// thread-safe.
    /// </summary>
    public sealed class DialogueAreaCounters
    {
        public const string FileName = "area.json";

        /// <summary>Config flag set when setup finishes the first time: the next translation run is the first run.</summary>
        public const string FirstRunPendingKey = "Area.FirstRunPending";

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private sealed class GameFile
        {
            public int Version { get; set; } = 1;
            /// <summary>How the area came to be: placed, followed, widened, drawn, typed, chosen, undone; and the answers kept/keptMine.</summary>
            public Dictionary<string, int> Areas { get; set; } = new();
            /// <summary>Window shape at each placement: 16:9, 16:10, 21:9, 4:3, other.</summary>
            public Dictionary<string, int> Shapes { get; set; } = new();
            /// <summary>What the check of a drawing found: fits, cutsText, tooBig, elsewhere, outsideGame.</summary>
            public Dictionary<string, int> Drawings { get; set; } = new();
            /// <summary>Why the area was placed again: moved, resized, corrected (the manifest changed).</summary>
            public Dictionary<string, int> PlacedAgain { get; set; } = new();
            public FirstRunReport FirstRun { get; set; }
        }

        private readonly Func<string, string> _fileFor;
        private readonly TimeSpan _debounce;
        private readonly object _lock = new();
        private readonly Dictionary<string, GameFile> _games = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
        private Timer _timer;

        public static DialogueAreaCounters Default { get; } =
            new(game => Path.Combine(GameDataPaths.GameDir(game), FileName), TimeSpan.FromSeconds(5));

        public DialogueAreaCounters(Func<string, string> fileFor, TimeSpan debounce)
        {
            _fileFor = fileFor ?? throw new ArgumentNullException(nameof(fileFor));
            _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
        }

        /// <summary>The shape bucket of a client area.</summary>
        public static string ShapeOf(int width, int height)
        {
            if (width <= 0 || height <= 0) return "other";
            double a = (double)width / height;
            if (Math.Abs(a - 16.0 / 9.0) < 0.02) return "16:9";
            if (Math.Abs(a - 16.0 / 10.0) < 0.02) return "16:10";
            if (a > 2.3 && a < 2.42) return "21:9";
            if (Math.Abs(a - 4.0 / 3.0) < 0.02) return "4:3";
            return "other";
        }

        /// <summary>16:9 and wider: where the centred band and the games agree (measured for WuWa and Endfield at 21:9).</summary>
        public static bool IsMeasuredShape(int width, int height) => height > 0 && (double)width / height >= 16.0 / 9.0 - 0.01;

        public static string NameOf(AreaOrigin origin) => origin switch
        {
            AreaOrigin.Followed => "followed",
            AreaOrigin.Widened => "widened",
            AreaOrigin.Drawn => "drawn",
            AreaOrigin.Typed => "typed",
            AreaOrigin.Chosen => "chosen",
            AreaOrigin.Undone => "undone",
            _ => "placed",
        };

        /// <summary>An answer to a note on the Start card that saves no area: <see cref="KeptWidened"/> or <see cref="KeptMine"/>.</summary>
        public void CountChoice(string gameId, string choice) => Change(gameId, f => Add(f.Areas, choice));

        /// <summary>"Keep it" on the widened frame's note.</summary>
        public const string KeptWidened = "keptWidened";
        /// <summary>"Keep mine" on the too-big or outside-the-game warning.</summary>
        public const string KeptMine = "keptMine";

        /// <summary>An area was saved; <paramref name="clientWidth"/> 0 when no game window was known.</summary>
        public void CountArea(string gameId, AreaOrigin origin, int clientWidth, int clientHeight) => Change(gameId, f =>
        {
            Add(f.Areas, NameOf(origin));
            if (clientWidth > 0 && origin is AreaOrigin.Placed or AreaOrigin.Followed) Add(f.Shapes, ShapeOf(clientWidth, clientHeight));
        });

        public void CountDrawing(string gameId, AreaVerdict verdict)
        {
            string key = verdict switch
            {
                AreaVerdict.Fits => "fits",
                AreaVerdict.CutsText => "cutsText",
                AreaVerdict.TooBig => "tooBig",
                AreaVerdict.Elsewhere => "elsewhere",
                AreaVerdict.OutsideGame => "outsideGame",
                _ => null,
            };
            if (key != null) Change(gameId, f => Add(f.Drawings, key));
        }

        /// <summary>Placed again: "moved", "resized" or "corrected".</summary>
        public void CountPlacedAgain(string gameId, string reason) => Change(gameId, f => Add(f.PlacedAgain, reason));

        /// <summary>The first run's report; written once per game, a later one is ignored.</summary>
        public bool RecordFirstRun(string gameId, FirstRunReport report)
        {
            bool written = false;
            Change(gameId, f =>
            {
                if (f.FirstRun != null) return;
                f.FirstRun = report;
                written = true;
            });
            return written;
        }

        /// <summary>One short line for the "Translation stopped" log line.</summary>
        public string Describe(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return "";
            lock (_lock)
            {
                GameFile f = Load(gameId);
                static string Part(Dictionary<string, int> d) => string.Join(" ", d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
                return $"Area: {Part(f.Areas)}; shapes {Part(f.Shapes)}; again {Part(f.PlacedAgain)}; drawings {Part(f.Drawings)}.";
            }
        }

        public int Get(string gameId, string bucket, string key)
        {
            lock (_lock)
            {
                GameFile f = Load(gameId);
                Dictionary<string, int> d = bucket switch
                {
                    "areas" => f.Areas,
                    "shapes" => f.Shapes,
                    "drawings" => f.Drawings,
                    _ => f.PlacedAgain,
                };
                return d.TryGetValue(key, out int n) ? n : 0;
            }
        }

        public FirstRunReport FirstRun(string gameId)
        {
            lock (_lock) return Load(gameId).FirstRun;
        }

        /// <summary>Writes what is waiting, now (the app is closing, a test reads the file).</summary>
        public void Flush()
        {
            lock (_lock)
            {
                foreach (string game in _dirty.ToArray())
                    if (Write(game)) _dirty.Remove(game);
            }
        }

        private static void Add(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

        private void Change(string gameId, Action<GameFile> change)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (_lock)
            {
                change(Load(gameId));
                _dirty.Add(gameId);
                if (_debounce == TimeSpan.Zero)
                {
                    if (Write(gameId)) _dirty.Remove(gameId);
                    return;
                }
                _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private bool Write(string gameId)
        {
            try
            {
                AtomicFile.WriteAllText(_fileFor(gameId), JsonSerializer.Serialize(_games[gameId], Json));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                Logger.Log.Warn($"Dialogue area counters of {gameId} were not saved: {ex.Message}");
                return false;
            }
        }

        /// <summary>Tolerant: a missing, cut-off, held or later-version file starts the counts again. They are only counts.</summary>
        private GameFile Load(string gameId)
        {
            if (_games.TryGetValue(gameId, out GameFile loaded)) return loaded;
            GameFile file = null;
            try
            {
                string path = _fileFor(gameId);
                if (File.Exists(path) || AtomicFile.RecoverInterruptedSave(path))
                    file = JsonSerializer.Deserialize<GameFile>(File.ReadAllText(path), Json);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                Logger.Log.Warn($"Dialogue area counters of {gameId} do not read ({ex.GetType().Name}); starting again.");
            }
            if (file == null || file.Version != 1) file = new GameFile();
            file.Areas ??= new();
            file.Shapes ??= new();
            file.Drawings ??= new();
            file.PlacedAgain ??= new();
            _games[gameId] = file;
            return file;
        }
    }
}
