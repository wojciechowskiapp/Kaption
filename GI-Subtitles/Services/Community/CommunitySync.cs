using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    /// <summary>The community side of a dictionary sync: send what is queued, then refresh accepted fixes.</summary>
    public static class CommunitySync
    {
        /// <summary>How long a fetch for one game and language covers before another trigger fetches again.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

        /// <summary>After a failed fetch: 5 minutes, doubling, at most 2 hours.</summary>
        public static TimeSpan RetryDelay(int failures) =>
            TimeSpan.FromMinutes(Math.Min(120, 5 * Math.Pow(2, Math.Max(0, Math.Min(failures, 8) - 1))));

        private static readonly Func<string> AppSession = () => App.LicenseService?.CurrentActivation?.DeviceSessionJwt;

        private enum Outcome { Applied, NotOffered, Failed }

        /// <summary>
        /// Runs alongside <c>DictionarySyncService.SyncAsync</c> on a pool thread, so reading the cache never delays the
        /// caller. Never throws; offline keeps the cache and tries again later.
        /// </summary>
        public static Task SyncAsync(string sessionJwt, string game, string language, CancellationToken ct) =>
            Task.Run(() => SyncCoreAsync(sessionJwt, game, language, ct));

        private static async Task SyncCoreAsync(string sessionJwt, string game, string language, CancellationToken ct)
        {
            try
            {
                // First, so queued fixes start their retry timer even when this sync fails.
                SuggestionSubmitter.Shared.Kick();
                var layers = TranslationLayerRegistry.For(game, language);
                if (string.IsNullOrEmpty(sessionJwt) || !Offered(layers) ||
                    !layers.TryBeginOverlayFetch(DateTime.UtcNow, force: true, RefreshInterval)) return;
                await RunClaimedAsync(new KaptionApiClient(), sessionJwt, layers, retryLater: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // App closing.
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Community fixes: sync step failed: {ex.Message}");
            }
        }

        /// <summary>
        /// For a game or language that comes into use after startup (another pack, or startup was offline): fetches in
        /// the background unless one is running or a recent one covers it. Never blocks.
        /// </summary>
        public static void RefreshIfStale(TranslationLayers layers, Func<string> session)
        {
            if (layers == null || !Offered(layers)) return;
            string jwt = session?.Invoke();
            if (string.IsNullOrEmpty(jwt) || !layers.TryBeginOverlayFetch(DateTime.UtcNow, force: false, RefreshInterval)) return;
            _ = Task.Run(async () =>
            {
                try { await RunClaimedAsync(new KaptionApiClient(), jwt, layers, retryLater: true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Log.Warn($"Community fixes: refresh failed: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Fetches accepted fixes changed since the cached cursor (all of them the first time) and folds them in, unless
        /// a fetch for the same game and language is running. Returns how many lines changed; any failure leaves the
        /// cached overlay as it was.
        /// </summary>
        public static async Task<int> RefreshOverlayAsync(ICommunityApi api, string sessionJwt, TranslationLayers layers, CancellationToken ct)
        {
            if (api == null || layers == null || string.IsNullOrEmpty(sessionJwt) || !Offered(layers)) return 0;
            if (!layers.TryBeginOverlayFetch(DateTime.UtcNow, force: true, RefreshInterval)) return 0;
            return (await RunClaimedAsync(api, sessionJwt, layers, retryLater: false, ct).ConfigureAwait(false)).Changed;
        }

        // The server keeps an overlay only where it reviews fixes; anywhere else the request would be a 400.
        private static bool Offered(TranslationLayers layers) => SuggestionRules.TakesSuggestions(layers.WireGame, layers.Lang);

        // Runs a claimed fetch and always ends the claim; a failure that may pass schedules another try in the app.
        private static async Task<(Outcome Outcome, int Changed)> RunClaimedAsync(
            ICommunityApi api, string jwt, TranslationLayers layers, bool retryLater, CancellationToken ct)
        {
            var result = (Outcome.Failed, 0);
            try
            {
                result = await FetchAsync(api, jwt, layers, ct).ConfigureAwait(false);
            }
            finally
            {
                bool failed = result.Item1 == Outcome.Failed;
                int failures = layers.EndOverlayFetch(DateTime.UtcNow, failed, failed ? RetryDelay(failures: 1) : RefreshInterval);
                if (failed && retryLater && !ct.IsCancellationRequested)
                {
                    var delay = RetryDelay(failures);
                    _ = Task.Delay(delay).ContinueWith(_ => RefreshIfStale(layers, AppSession), TaskScheduler.Default);
                }
            }
            return result;
        }

        private static async Task<(Outcome, int)> FetchAsync(ICommunityApi api, string sessionJwt, TranslationLayers layers, CancellationToken ct)
        {
            layers.EnsureLoaded();
            CommunityOverlayPage page;
            try
            {
                page = await api.GetCommunityOverlayAsync(sessionJwt, layers.WireGame, layers.Lang, layers.CommunitySince, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiValidationException ex) when ((int)ex.StatusCode == 404 || (int)ex.StatusCode == 400)
            {
                // 404: route not live yet. 400: this game or language takes no community fixes.
                Logger.Log.Info($"Community fixes for {layers.WireGame}/{layers.Lang}: none offered (HTTP {(int)ex.StatusCode}); keeping the cache.");
                return (Outcome.NotOffered, 0);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Community fixes for {layers.WireGame}/{layers.Lang} not refreshed ({ex.GetType().Name}: {ex.Message}); keeping the cache.");
                return (Outcome.Failed, 0);
            }

            try
            {
                int changed = layers.ApplyOverlay(page);
                Logger.Log.Info($"Community fixes for {layers.WireGame}/{layers.Lang}: {page.Entries.Count} received" +
                                $"{(page.Full ? " (full set)" : "")}, {changed} lines changed, {layers.CommunityCount} in use.");
                return (Outcome.Applied, changed);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Community fixes for {layers.WireGame}/{layers.Lang} could not be saved: {ex.Message}");
                return (Outcome.Failed, 0);
            }
        }
    }
}
