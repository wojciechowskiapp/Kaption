using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Config;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    public sealed class LatestPack
    {
        public string Game { get; init; }
        public string Language { get; init; }
        public string PackVersion { get; init; }
        public string GameVersion { get; init; }
    }

    public sealed class ReleaseNote
    {
        public string ReleaseId { get; init; }
        public string Slug { get; init; }
        public string Game { get; init; }
        public string GameVersion { get; init; }
        public string Kind { get; init; }
        public string Tagline { get; init; }
        public long NewLines { get; init; }
        public DateTimeOffset PublishedAt { get; init; }
    }

    /// <summary>
    /// The Start page's two public, no-sign-in reads from api.kaption.one: the latest pack per game and the newest
    /// release note. Each is fetched at most once per launch, off the UI thread, with a short timeout and no retries;
    /// any failure yields null and the card simply stays hidden.
    /// </summary>
    public static class DashboardFeeds
    {
        private static readonly HttpClient Http = CreateClient();
        private static Task<IReadOnlyList<LatestPack>> _packs;
        private static readonly Dictionary<string, Task<ReleaseNote>> _notes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            string v = typeof(DashboardFeeds).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"Kaption/{v}");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        private static string ApiBase => (Config.Get("ApiUrl", "https://api.kaption.one") ?? "https://api.kaption.one").TrimEnd('/');

        public static Task<IReadOnlyList<LatestPack>> LatestPacksAsync()
        {
            lock (Gate) return _packs ??= Task.Run(FetchPacksAsync);
        }

        public static Task<ReleaseNote> LatestReleaseNoteAsync(string language)
        {
            string lang = string.IsNullOrWhiteSpace(language) ? "pl" : language.ToLowerInvariant();
            lock (Gate)
            {
                if (!_notes.TryGetValue(lang, out var t)) _notes[lang] = t = Task.Run(() => FetchNoteAsync(lang));
                return t;
            }
        }

        private static async Task<IReadOnlyList<LatestPack>> FetchPacksAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{ApiBase}/api/app/supported-versions", cts.Token).ConfigureAwait(false));
                return ParsePacks(doc.RootElement);
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Dashboard: latest pack versions unavailable ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        private static async Task<ReleaseNote> FetchNoteAsync(string lang)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string url = $"{ApiBase}/api/app/release-notes?language={Uri.EscapeDataString(lang)}&limit=1";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, cts.Token).ConfigureAwait(false));
                return ParseNote(doc.RootElement);
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Dashboard: release notes unavailable ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        public static IReadOnlyList<LatestPack> ParsePacks(JsonElement root)
        {
            var list = new List<LatestPack>();
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var it in items.EnumerateArray())
            {
                string game = Str(it, "game"), version = Str(it, "pack_version");
                if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(version)) continue;
                list.Add(new LatestPack { Game = game, Language = Str(it, "language"), PackVersion = version, GameVersion = Str(it, "game_version") });
            }
            return list;
        }

        public static ReleaseNote ParseNote(JsonElement root)
        {
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            foreach (var it in items.EnumerateArray())
            {
                string id = Str(it, "release_id");
                if (string.IsNullOrEmpty(id)) continue;
                long published = it.TryGetProperty("published_at", out var p) && p.TryGetInt64(out long ts) ? ts : 0;
                long newLines = it.TryGetProperty("facts", out var f) && f.ValueKind == JsonValueKind.Object
                                && f.TryGetProperty("new_lines", out var nl) && nl.TryGetInt64(out long n) ? n : 0;
                string tagline = it.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "tagline") : null;
                return new ReleaseNote
                {
                    ReleaseId = id, Slug = Str(it, "slug"), Game = Str(it, "game"), GameVersion = Str(it, "game_version"),
                    Kind = Str(it, "kind"), Tagline = tagline, NewLines = newLines,
                    PublishedAt = published > 0 ? DateTimeOffset.FromUnixTimeSeconds(published) : DateTimeOffset.MinValue,
                };
            }
            return null;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
