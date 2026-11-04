using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Community;

namespace GI_Subtitles.Services.Network
{
    /// <summary>Body of <c>POST /api/community/suggestions</c> (design §4.2).</summary>
    public sealed class CommunitySuggestionRequest
    {
        [JsonPropertyName("game")] public string Game { get; init; }
        [JsonPropertyName("lang")] public string Lang { get; init; }
        [JsonPropertyName("enText")] public string EnText { get; init; }
        [JsonPropertyName("currentText")] public string CurrentText { get; init; }
        [JsonPropertyName("suggestedText")] public string SuggestedText { get; init; }
        [JsonPropertyName("speaker")] public string Speaker { get; init; }
        [JsonPropertyName("contextPrev")] public string ContextPrev { get; init; }
        [JsonPropertyName("note")] public string Note { get; init; }

        /// <summary>Empty optional fields are left out of the body.</summary>
        public static CommunitySuggestionRequest From(PendingSuggestion s) => new CommunitySuggestionRequest
        {
            Game = s.Game,
            Lang = s.Lang,
            EnText = s.EnText,
            CurrentText = s.CurrentText ?? "",
            SuggestedText = s.SuggestedText,
            Speaker = NullIfEmpty(s.Speaker),
            ContextPrev = NullIfEmpty(s.ContextPrev),
            Note = NullIfEmpty(s.Note),
        };

        private static string NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>The two community calls the desktop makes; a seam so the queue and the overlay sync test without a server.</summary>
    public interface ICommunityApi
    {
        Task SubmitSuggestionAsync(string sessionJwt, CommunitySuggestionRequest body, CancellationToken ct);

        Task<CommunityOverlayPage> GetCommunityOverlayAsync(
            string sessionJwt, string game, string lang, string since, CancellationToken ct);
    }

    public sealed partial class KaptionApiClient : ICommunityApi
    {
        /// <summary>
        /// POST /api/community/suggestions with the device session. The server keeps one pending row per line and
        /// user, so a retried send updates instead of duplicating.
        /// </summary>
        public async Task SubmitSuggestionAsync(string sessionJwt, CommunitySuggestionRequest body, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionJwt)) throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            if (body == null) throw new ArgumentNullException(nameof(body));
            await PostJsonAsync<JsonElement>("/api/community/suggestions", body, sessionJwt, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// GET /api/community/overlay?game=&amp;lang=&amp;since= (device session). Without <paramref name="since"/> the
        /// server sends every live line (<c>full</c>); with it, lines changed since then plus withdrawn ones.
        /// </summary>
        public async Task<CommunityOverlayPage> GetCommunityOverlayAsync(
            string sessionJwt, string game, string lang, string since, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sessionJwt)) throw new ArgumentException("sessionJwt required", nameof(sessionJwt));
            var root = await GetJsonAsync<JsonElement>(BuildOverlayUrl(game, lang, since), sessionJwt, ct).ConfigureAwait(false);
            return ParseOverlay(root, requestedFull: string.IsNullOrWhiteSpace(since));
        }

        internal static string BuildOverlayUrl(string game, string lang, string since)
        {
            string url = $"/api/community/overlay?game={Uri.EscapeDataString(game ?? "")}&lang={Uri.EscapeDataString(lang ?? "")}";
            if (!string.IsNullOrWhiteSpace(since)) url += $"&since={Uri.EscapeDataString(since)}";
            return url;
        }

        /// <summary>
        /// <c>{game, lang, full, until, items:[{en,target,acceptedAt}], removed:[{en,removedAt}]}</c>. A withdrawn
        /// line comes back as an entry with an empty target. <c>full</c> falls back to whether <c>since</c> was sent.
        /// An item whose text cannot be read (half a surrogate pair) is dropped; the rest of the page applies.
        /// </summary>
        internal static CommunityOverlayPage ParseOverlay(JsonElement root, bool requestedFull)
        {
            if (root.ValueKind != JsonValueKind.Object) return CommunityOverlayPage.Empty;
            var list = new List<CommunityEntry>();
            foreach (var item in LayerFiles.Items(root, "items"))
            {
                var entry = new CommunityEntry(LayerFiles.Str(item, "en"), LayerFiles.Str(item, "target"), LayerFiles.Str(item, "acceptedAt"));
                if (entry.En.Length > 0 && entry.Target.Length > 0) list.Add(entry);
            }
            foreach (var item in LayerFiles.Items(root, "removed"))
            {
                var removal = new CommunityEntry(LayerFiles.Str(item, "en"), "", LayerFiles.Str(item, "removedAt"));
                if (removal.En.Length > 0) list.Add(removal);
            }
            bool full = LayerFiles.Bool(root, "full") ?? requestedFull;
            return new CommunityOverlayPage(list, LayerFiles.Str(root, "until"), full);
        }
    }
}
