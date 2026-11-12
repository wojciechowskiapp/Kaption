using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Data;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Today
{
    /// <summary>One period of the site's week file: what it is, when it runs, and its article's path on the site.</summary>
    public sealed record WeekPeriod(string Game, string Kind, string Title, string Label, DateTimeOffset? StartsAt, DateTimeOffset EndsAt, string Path);

    /// <summary>
    /// kaption.one/data/today/week.json (landing/src/pages/data/today/week.json.ts): the periods official posts state,
    /// the same rows the site's today page lists, per language. Parsing is tolerant: a bad row is skipped, a bad file
    /// is null.
    /// </summary>
    public sealed class TodayWeek
    {
        public const int Schema = 1;
        /// <summary>The published file is some tens of KB; anything far larger is not ours.</summary>
        public const int MaxBytes = 2 * 1024 * 1024;
        public const int MaxRows = 400;

        public IReadOnlyList<WeekPeriod> English { get; init; } = Array.Empty<WeekPeriod>();
        public IReadOnlyList<WeekPeriod> Polish { get; init; } = Array.Empty<WeekPeriod>();

        /// <summary>The rows in the interface language; a Polish interface without Polish rows reads the English ones.</summary>
        public IReadOnlyList<WeekPeriod> For(string uiLanguage) =>
            CharacterPageLink.IsPolish(uiLanguage) && Polish.Count > 0 ? Polish : English;

        public static TodayWeek Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int v) || v != Schema) return null;
                // Both lists must be lists (empty is fine): a file with one missing or of another shape is not ours,
                // and must not replace the last good copy.
                if (!IsList(root, "en") || !IsList(root, "pl")) return null;
                return new TodayWeek { English = Rows(root, "en"), Polish = Rows(root, "pl") };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsList(JsonElement root, string name) =>
            root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array;

        private static IReadOnlyList<WeekPeriod> Rows(JsonElement root, string lang)
        {
            var rows = new List<WeekPeriod>();
            if (!root.TryGetProperty(lang, out var list) || list.ValueKind != JsonValueKind.Array) return rows;
            foreach (var e in list.EnumerateArray())
            {
                if (rows.Count >= MaxRows) break;
                if (e.ValueKind != JsonValueKind.Object) continue;
                string game = Str(e, "game"), title = Str(e, "title"), path = Str(e, "path");
                // A path is a page of the site: "/news/slug/". Anything else (a host, a scheme) is not a row.
                if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(title) || path == null
                    || !path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!Time(e, "endsAt", out var ends)) continue;
                // No start (absent or null) means it runs already. A start that is there and cannot be read is a bad
                // row: read as "no start" it would list something upcoming as running.
                DateTimeOffset? starts = null;
                if (e.TryGetProperty("startsAt", out var rawStart) && rawStart.ValueKind != JsonValueKind.Null)
                {
                    if (!Time(e, "startsAt", out var s)) continue;
                    starts = s;
                }
                if (starts > ends) continue;
                rows.Add(new WeekPeriod(game.Trim().ToLowerInvariant(), Str(e, "kind") ?? "", title.Trim(), Str(e, "label")?.Trim(), starts, ends, path));
            }
            return rows;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Time(JsonElement e, string name, out DateTimeOffset at) =>
            DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
    }

    /// <summary>
    /// The week file, read at most once in <see cref="FreshFor"/> and kept on disk (%APPDATA%\Kaption\today-week.json).
    /// Never throws: a failed read falls back to the last good copy, and without one the card has no "ending soon".
    /// </summary>
    public static class TodayWeekData
    {
        /// <summary>The site is rebuilt when news lands; a copy this old is read again.</summary>
        public static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
        /// <summary>After a read that failed or gave an unreadable file, the next try waits this long (the copy on disk serves meanwhile).</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static DateTime _noTryBeforeUtc;
        // The last good read of this run: serves when the copy on disk could not be written (a full or read-only disk).
        private static (TodayWeek Week, DateTime AtUtc)? _lastRead;

        public static string File => Path.Combine(GameDataPaths.Root, "today-week.json");

        private static string Url => (Config.Get("AppUrl", CharacterPageLink.Site) ?? CharacterPageLink.Site).TrimEnd('/') + "/data/today/week.json";

        public static async Task<TodayWeek> GetAsync(DateTime nowUtc)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                TodayWeek cached = TodayWeek.Parse(FarmTodayData.ReadText(File));
                if (cached != null && IsFresh(WrittenUtc(), nowUtc)) return cached;
                if (_lastRead is { } kept && IsFresh(kept.AtUtc, nowUtc)) return kept.Week;
                // A failed read is not repeated by every caller that follows: they get the copy on disk for a while.
                if (!MayTry(nowUtc, _noTryBeforeUtc)) return cached ?? _lastRead?.Week;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var resp = await FarmTodayData.Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    await using Stream body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    string text = await ReadBoundedAsync(body, TodayWeek.MaxBytes, cts.Token).ConfigureAwait(false);
                    TodayWeek fresh = text == null ? null : TodayWeek.Parse(text);
                    if (fresh != null)
                    {
                        FarmTodayData.WriteAtomic(File, text);
                        _lastRead = (fresh, nowUtc);
                        return fresh;
                    }
                    Logger.Log.Warn(text == null
                        ? "Today card: the week file from kaption.one is far too large; keeping the last copy."
                        : "Today card: the week file from kaption.one is not readable; keeping the last copy.");
                }
                catch (Exception ex)
                {
                    Logger.Log.Info($"Today card: week file not fetched ({ex.GetType().Name}: {ex.Message}).");
                }
                _noTryBeforeUtc = nowUtc + RetryAfter;
                return cached ?? _lastRead?.Week;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Whether a read may be tried now; a clock set back past the mark may try again.</summary>
        public static bool MayTry(DateTime nowUtc, DateTime noTryBeforeUtc) =>
            nowUtc >= noTryBeforeUtc || noTryBeforeUtc - nowUtc > RetryAfter;

        /// <summary>
        /// The stream as text, or null once it runs past <paramref name="maxBytes"/>: the decoded body is counted as
        /// it arrives, so a response that unpacks to something huge is dropped before it is held in memory.
        /// </summary>
        public static async Task<string> ReadBoundedAsync(Stream body, int maxBytes, CancellationToken ct)
        {
            using var kept = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > maxBytes) return null;
                kept.Write(buffer, 0, read);
            }
            return System.Text.Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
        }

        /// <summary>A copy written within <see cref="FreshFor"/>, and not after the clock (a clock set back reads again).</summary>
        public static bool IsFresh(DateTime? writtenUtc, DateTime nowUtc) =>
            writtenUtc is DateTime w && w <= nowUtc.AddMinutes(5) && nowUtc - w < FreshFor;

        private static DateTime? WrittenUtc()
        {
            try { return System.IO.File.Exists(File) ? System.IO.File.GetLastWriteTimeUtc(File) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }
    }
}
