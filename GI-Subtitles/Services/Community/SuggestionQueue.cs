using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    /// <summary>A fix waiting to reach <c>POST /api/community/suggestions</c>.</summary>
    public sealed record PendingSuggestion
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        /// <summary>Wire id (genshin, starrail, zzz).</summary>
        public string Game { get; init; } = "";
        public string Lang { get; init; } = "";
        public string EnText { get; init; } = "";
        public string CurrentText { get; init; } = "";
        public string SuggestedText { get; init; } = "";
        public string Speaker { get; init; } = "";
        public string ContextPrev { get; init; } = "";
        public string Note { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public int Attempts { get; init; }
        public DateTimeOffset NextAttemptAt { get; init; }
        public string LastError { get; init; } = "";

        // EnText is sent exactly as the pack key; edge whitespace does not make it another line.
        internal bool SameLine(PendingSuggestion other) =>
            other != null &&
            string.Equals(Game, other.Game, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Lang, other.Lang, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((EnText ?? "").Trim(), (other.EnText ?? "").Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Suggestions that have not reached the server yet, kept in <c>%APPDATA%\Kaption\community-queue.json</c> so an
    /// offline evening or a closed app loses nothing. Thread-safe; every change is written through.
    /// </summary>
    public sealed class SuggestionQueue
    {
        public const int MaxItems = 200;
        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxRetry = TimeSpan.FromHours(6);

        private readonly object _gate = new object();
        private readonly string _path;
        private readonly Func<DateTimeOffset> _clock;
        private List<PendingSuggestion> _items = new List<PendingSuggestion>();
        private bool _loaded;

        public SuggestionQueue(string path, Func<DateTimeOffset> clock = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public string FilePath => _path;

        public int Count
        {
            get { lock (_gate) { EnsureLoadedLocked(); return _items.Count; } }
        }

        public IReadOnlyList<PendingSuggestion> Items
        {
            get { lock (_gate) { EnsureLoadedLocked(); return _items.ToList(); } }
        }

        /// <summary>1 min, doubling per failed attempt, at most 6 h.</summary>
        public static TimeSpan Backoff(int attempts)
        {
            if (attempts <= 1) return FirstRetry;
            double minutes = FirstRetry.TotalMinutes * Math.Pow(2, Math.Min(attempts - 1, 16));
            return TimeSpan.FromMinutes(Math.Min(minutes, MaxRetry.TotalMinutes));
        }

        /// <summary>Adds a suggestion, due now. A newer one for the same line replaces the waiting one.</summary>
        public void Enqueue(PendingSuggestion suggestion)
        {
            if (suggestion == null) throw new ArgumentNullException(nameof(suggestion));
            lock (_gate)
            {
                EnsureLoadedLocked();
                var now = _clock();
                _items.RemoveAll(i => i.SameLine(suggestion));
                _items.Add(suggestion with
                {
                    CreatedAt = suggestion.CreatedAt == default ? now : suggestion.CreatedAt,
                    NextAttemptAt = now,
                    Attempts = 0,
                    LastError = "",
                });
                TrimToCapLocked();
                PersistLocked();
            }
        }

        private void TrimToCapLocked()
        {
            int over = _items.Count - MaxItems;
            if (over <= 0) return;
            _items.RemoveRange(0, over);
            Logger.Log.Warn($"The fix send queue is full ({MaxItems}); the {over} oldest waiting suggestion(s) were dropped.");
        }

        /// <summary>Items whose next attempt is due, oldest first; expired ones are dropped on the way.</summary>
        public IReadOnlyList<PendingSuggestion> Due()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                var now = _clock();
                if (_items.RemoveAll(i => now - i.CreatedAt > MaxAge) > 0)
                {
                    Logger.Log.Info("Fix suggestions older than 30 days were dropped from the send queue.");
                    PersistLocked();
                }
                return _items.Where(i => i.NextAttemptAt <= now).OrderBy(i => i.CreatedAt).ToList();
            }
        }

        /// <summary>The earliest time anything is due, or null when the queue is empty.</summary>
        public DateTimeOffset? NextDue()
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _items.Count == 0 ? (DateTimeOffset?)null : _items.Min(i => i.NextAttemptAt);
            }
        }

        public void Complete(string id) => Remove(id);

        /// <summary>Takes back a not-yet-sent suggestion for the line (its fix was removed); true when one was waiting.</summary>
        public bool Withdraw(string game, string lang, string enText)
        {
            var probe = new PendingSuggestion { Game = game ?? "", Lang = lang ?? "", EnText = enText ?? "" };
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_items.RemoveAll(i => i.SameLine(probe)) == 0) return false;
                PersistLocked();
                return true;
            }
        }

        /// <summary>Waiting suggestions for one game and language.</summary>
        public int CountFor(string game, string lang)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _items.Count(i => string.Equals(i.Game, game, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(i.Lang, lang, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Drop(string id, string reason)
        {
            if (Remove(id)) Logger.Log.Warn($"A fix suggestion was not accepted by the server and was dropped: {reason}");
        }

        /// <summary>Counts a failed attempt and schedules the next one; returns when it is due.</summary>
        public DateTimeOffset Retry(string id, string error, TimeSpan? notBefore = null)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                var now = _clock();
                int index = _items.FindIndex(i => i.Id == id);
                if (index < 0) return now;
                var item = _items[index];
                int attempts = item.Attempts + 1;
                var wait = Backoff(attempts);
                if (notBefore.HasValue && notBefore.Value > wait) wait = notBefore.Value;
                var next = now + wait;
                _items[index] = item with { Attempts = attempts, NextAttemptAt = next, LastError = error ?? "" };
                PersistLocked();
                return next;
            }
        }

        /// <summary>Moves every waiting item to no earlier than <paramref name="time"/> (one offline failure pauses all).</summary>
        public void DeferAll(DateTimeOffset time)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                bool changed = false;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].NextAttemptAt >= time) continue;
                    _items[i] = _items[i] with { NextAttemptAt = time };
                    changed = true;
                }
                if (changed) PersistLocked();
            }
        }

        private bool Remove(string id)
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                if (_items.RemoveAll(i => i.Id == id) == 0) return false;
                PersistLocked();
                return true;
            }
        }

        // ── file ─────────────────────────────────────────────────────────

        // A file that cannot be read right now is never overwritten; what was queued meanwhile is merged in later.
        private void EnsureLoadedLocked()
        {
            if (_loaded) return;
            var fromDisk = Read(_path, out var state);
            if (state == LayerFileState.Unreadable) return;
            bool hadPending = _items.Count > 0;
            // Queued while the file was unreadable, so newer than its copy of the same line.
            foreach (var pending in _items)
            {
                fromDisk.RemoveAll(d => d.Id == pending.Id || d.SameLine(pending));
                fromDisk.Add(pending);
            }
            _items = fromDisk;
            TrimToCapLocked();
            _loaded = true;
            if (hadPending || state == LayerFileState.Corrupt) PersistLocked();
        }

        private void PersistLocked()
        {
            if (!_loaded) return;
            try
            {
                AtomicFile.WriteAllBytes(_path, LayerFiles.Build(w =>
                {
                    w.WriteNumber("schema", LayerFiles.Schema);
                    w.WriteStartArray("items");
                    foreach (var i in _items)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", i.Id);
                        w.WriteString("game", i.Game);
                        w.WriteString("lang", i.Lang);
                        w.WriteString("enText", i.EnText);
                        w.WriteString("currentText", i.CurrentText);
                        w.WriteString("suggestedText", i.SuggestedText);
                        if (i.Speaker.Length > 0) w.WriteString("speaker", i.Speaker);
                        if (i.ContextPrev.Length > 0) w.WriteString("contextPrev", i.ContextPrev);
                        if (i.Note.Length > 0) w.WriteString("note", i.Note);
                        w.WriteString("createdAt", Iso(i.CreatedAt));
                        w.WriteNumber("attempts", i.Attempts);
                        w.WriteString("nextAttemptAt", Iso(i.NextAttemptAt));
                        if (i.LastError.Length > 0) w.WriteString("lastError", i.LastError);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still queued in memory; the next change writes it again.
                Logger.Log.Warn($"Could not save the fix send queue: {ex.Message}");
            }
        }

        private static List<PendingSuggestion> Read(string path, out LayerFileState state)
        {
            var items = new List<PendingSuggestion>();
            if (!LayerFiles.TryParse(path, out var doc, out state)) return items;
            using (doc)
            {
                foreach (var e in LayerFiles.Items(doc.RootElement, "items"))
                {
                    string en = LayerFiles.Str(e, "enText"), suggested = LayerFiles.Str(e, "suggestedText");
                    string game = LayerFiles.Str(e, "game"), lang = LayerFiles.Str(e, "lang");
                    if (en.Length == 0 || suggested.Length == 0 || game.Length == 0 || lang.Length == 0) continue;
                    var created = CommunityEntry.ParseTime(LayerFiles.Str(e, "createdAt"));
                    if (created == null) continue;
                    string id = LayerFiles.Str(e, "id");
                    items.Add(new PendingSuggestion
                    {
                        Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N"),
                        Game = game,
                        Lang = lang,
                        EnText = en,
                        CurrentText = LayerFiles.Str(e, "currentText"),
                        SuggestedText = suggested,
                        Speaker = LayerFiles.Str(e, "speaker"),
                        ContextPrev = LayerFiles.Str(e, "contextPrev"),
                        Note = LayerFiles.Str(e, "note"),
                        CreatedAt = created.Value,
                        Attempts = Math.Max(0, LayerFiles.Int(e, "attempts")),
                        NextAttemptAt = CommunityEntry.ParseTime(LayerFiles.Str(e, "nextAttemptAt")) ?? created.Value,
                        LastError = LayerFiles.Str(e, "lastError"),
                    });
                }
            }
            return items;
        }

        private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }
}
