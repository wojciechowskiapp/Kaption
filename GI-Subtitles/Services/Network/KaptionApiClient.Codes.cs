using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Codes;

namespace GI_Subtitles.Services.Network
{
    public sealed partial class KaptionApiClient
    {
        /// <summary>The codes card's read is cosmetic: one try, this long, then the poller's own backoff.</summary>
        public static readonly TimeSpan CodesRequestTimeout = TimeSpan.FromSeconds(10);
        /// <summary>The list is a few KB; an answer past this is not the codes list.</summary>
        public const long MaxCodesBodyBytes = 1024 * 1024;
        /// <summary>The capped read's buffer: at most this much is read past the cap before an oversized body is refused.</summary>
        public const int CodesReadChunkBytes = 16 * 1024;

        /// <summary>
        /// GET /api/app/codes?game= (public, no session; backend/src/routes/news.ts, cached 60 s at the edge): one game's
        /// active codes. A single attempt with no retries and no logging here; the caller (CodesPoller) logs and backs
        /// off. Throws <see cref="RateLimitedException"/> for 429, and for 503 with a Retry-After (the server's wait);
        /// <see cref="ApiUnavailableException"/> for the network, a timeout or another 5xx; <see cref="CodesFormatException"/>
        /// for a body that is not the list; <see cref="ApiValidationException"/> for any other status.
        /// </summary>
        public async Task<RedeemCodeList> GetRedeemCodesAsync(string game, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(game)) throw new ArgumentException("game required", nameof(game));
            string wire = game.Trim().ToLowerInvariant();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CodesRequestTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, ResolveUrl("/api/app/codes?game=" + Uri.EscapeDataString(wire)));
            HttpResponseMessage response;
            try
            {
                response = await _sharedHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiUnavailableException("codes: network error", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ApiUnavailableException("codes: timed out", ex);
            }
            using (response)
            {
                int status = (int)response.StatusCode;
                TimeSpan? wait = RetryAfterOf(response.Headers.RetryAfter, DateTimeOffset.UtcNow);
                if (status == 429 || (status == 503 && wait.HasValue))
                    throw new RateLimitedException(wait ?? TimeSpan.Zero);
                if (status >= 500)
                    throw new ApiUnavailableException($"codes: HTTP {status}", new HttpRequestException($"HTTP {status}", null, response.StatusCode));
                if (!response.IsSuccessStatusCode)
                    throw new ApiValidationException(response.StatusCode, new ApiError());
                string body;
                try
                {
                    body = await ReadCappedAsync(response.Content, MaxCodesBodyBytes, timeout.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new ApiUnavailableException("codes: connection dropped", ex);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new ApiUnavailableException("codes: timed out", ex);
                }
                return RedeemCodeList.Parse(body, wire);
            }
        }

        /// <summary>
        /// The body as UTF-8 text (RFC 8259), never more than <paramref name="maxBytes"/> of it in memory: a declared
        /// Content-Length past the cap is refused before reading, and a body without one (chunked) is refused as soon as
        /// the bytes read pass the cap, at most one <see cref="CodesReadChunkBytes"/> buffer later.
        /// </summary>
        public static async Task<string> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
        {
            if (content == null) return string.Empty;
            if (content.Headers.ContentLength is long declared && declared > maxBytes)
                throw new CodesFormatException($"body of {declared} bytes, over the {maxBytes}-byte cap");
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var kept = new MemoryStream();
            byte[] buffer = new byte[CodesReadChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > maxBytes)
                    throw new CodesFormatException($"body over the {maxBytes}-byte cap");
                kept.Write(buffer, 0, read);
            }
            string text = Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
            return text.Length > 0 && text[0] == (char)0xFEFF ? text.Substring(1) : text;
        }

        /// <summary>
        /// A Retry-After header as a wait from <paramref name="now"/>: delta-seconds or an HTTP date (RFC 9110 §10.2.3).
        /// A date already past is no wait (zero); no header is null.
        /// </summary>
        public static TimeSpan? RetryAfterOf(RetryConditionHeaderValue header, DateTimeOffset now)
        {
            if (header == null) return null;
            if (header.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (header.Date is DateTimeOffset date) return date > now ? date - now : TimeSpan.Zero;
            return null;
        }
    }
}
