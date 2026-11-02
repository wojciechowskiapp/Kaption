using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Data;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// Game icons (characters, sets, weapons) fetched once over HTTPS and kept on disk. Never throws:
    /// any failure returns null and the view shows its fallback. Failures are not retried for a while.
    /// </summary>
    public sealed class CharacterImageCache
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        /// <summary>Full-body art and ambience images, which are larger than icons.</summary>
        public const int LargeMaxBytes = 4 * 1024 * 1024;
        private const int MaxParallel = 4;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

        private static readonly HttpClient SharedHttp = CreateHttp();
        private static readonly ConcurrentDictionary<string, CharacterImageCache> PerGame =
            new ConcurrentDictionary<string, CharacterImageCache>(StringComparer.OrdinalIgnoreCase);

        private readonly string _dir;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _parallel = new SemaphoreSlim(MaxParallel, MaxParallel);
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight =
            new ConcurrentDictionary<string, Lazy<Task<string>>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failedAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public CharacterImageCache(string directory, HttpClient http)
        {
            _dir = directory ?? throw new ArgumentNullException(nameof(directory));
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>The app-wide cache for a game: %APPDATA%\Kaption\&lt;Game&gt;\icons.</summary>
        public static CharacterImageCache ForGame(string game) =>
            PerGame.GetOrAdd(game ?? "", g => new CharacterImageCache(Path.Combine(GameDataPaths.GameDir(g), "icons"), SharedHttp));

        /// <summary>Local path if already downloaded; never touches the network.</summary>
        public string CachedPathOrNull(string url)
        {
            string path = PathFor(url);
            return path != null && File.Exists(path) ? path : null;
        }

        public Task<string> GetAsync(string url) => GetAsync(url, MaxBytes);

        public Task<string> GetAsync(string url, int maxBytes)
        {
            string path = PathFor(url);
            if (path == null) return Task.FromResult<string>(null);
            if (File.Exists(path)) return Task.FromResult(path);
            if (_failedAt.TryGetValue(url, out var at) && DateTime.UtcNow - at < RetryAfter) return Task.FromResult<string>(null);

            var lazy = _inFlight.GetOrAdd(url, u => new Lazy<Task<string>>(() => DownloadAsync(u, path, maxBytes)));
            return lazy.Value;
        }

        private async Task<string> DownloadAsync(string url, string path, int maxBytes)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
            await _parallel.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var cts = new CancellationTokenSource(Timeout))
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                {
                    string type = resp.Content?.Headers.ContentType?.MediaType ?? "";
                    if (!resp.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        return Fail(url, $"HTTP {(int)resp.StatusCode} {type}");
                    if (resp.Content.Headers.ContentLength > maxBytes) return Fail(url, "too large");

                    byte[] body = await ReadCappedAsync(resp, maxBytes, cts.Token).ConfigureAwait(false);
                    if (body == null) return Fail(url, "too large");
                    if (!LooksLikeImage(body)) return Fail(url, "not an image");

                    Directory.CreateDirectory(_dir);
                    File.WriteAllBytes(tmp, body);
                    File.Move(tmp, path, overwrite: true);
                    return path;
                }
            }
            catch (Exception ex)
            {
                return Fail(url, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _parallel.Release();
                TryDelete(tmp);
                _inFlight.TryRemove(url, out _);
            }
        }

        private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
        {
            using (var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > maxBytes) return null;
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }

        // PNG, JPEG, GIF, WebP or ICO by signature; a CDN error page served as image/* is rejected.
        internal static bool LooksLikeImage(byte[] b)
        {
            if (b == null || b.Length < 12) return false;
            bool png = b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
            bool jpg = b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
            bool gif = b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38;
            bool webp = b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                        b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50;
            bool ico = b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00; // favicons
            return png || jpg || gif || webp || ico;
        }

        private string PathFor(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Contains("/../") || url.Contains("/./")) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            string ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (ext != ".png" && ext != ".webp" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif") ext = ".img";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                return Path.Combine(_dir, Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ext);
            }
        }

        private string Fail(string url, string why)
        {
            _failedAt[url] = DateTime.UtcNow;
            Logger.Log.Warn($"Icon not cached ({why}): {url}");
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kaption/" + typeof(CharacterImageCache).Assembly.GetName().Version);
            return http;
        }
    }
}
