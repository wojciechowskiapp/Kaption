using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// One-shot loopback HTTP listener used by the desktop OAuth flow.
    ///
    /// Lifecycle:
    ///   1. <see cref="BuildActivationUrl(string, string)"/> picks a free ephemeral
    ///      port on 127.0.0.1, generates a cryptographic state nonce, and returns
    ///      the URL the user should open in their browser.
    ///   2. <see cref="WaitForTokenAsync"/> starts the listener and blocks (async)
    ///      until the browser posts back to /callback with a token, or the timeout
    ///      elapses, or the caller cancels.
    ///   3. The instance is disposable — calling Dispose stops the listener and
    ///      releases the bound port.
    ///
    /// Security:
    ///   * The listener binds to 127.0.0.1 only, never to 0.0.0.0. No LAN exposure.
    ///   * A 256-bit random state nonce is generated and checked on the callback —
    ///     prevents a malicious local process from impersonating the OAuth return.
    ///   * Non-matching paths, methods, and state values get a 404/400 and do NOT
    ///     complete the wait — so a drive-by browser probe can't spoof a token.
    ///   * Localhost HttpListener prefixes do NOT require administrator on Windows.
    ///
    /// Why not use IAsyncResult/GetContextAsync's full surface? Because we want
    /// strict control over which request completes the wait (correct path, correct
    /// state); everything else is rejected and the listener keeps waiting.
    /// </summary>
    public sealed class LoopbackAuthListener : IDisposable
    {
        private const string CallbackPath = "/callback";

        private readonly int _port;
        private readonly string _state;
        private readonly HttpListener _listener;
        private bool _disposed;

        /// <summary>Port the listener is (or will be) bound to. Read-only after construction.</summary>
        public int Port => _port;

        /// <summary>The random state nonce that must appear in the browser callback.</summary>
        public string State => _state;

        /// <summary>
        /// Construct the listener. Reserves a free ephemeral port on 127.0.0.1
        /// but does NOT yet start receiving requests — call
        /// <see cref="WaitForTokenAsync"/> for that.
        /// </summary>
        public LoopbackAuthListener()
        {
            _state = GenerateStateNonce();
            _port = PickFreeLoopbackPort();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        }

        /// <summary>
        /// Build the activation URL the user should open in their browser.
        /// Points at the Kaption web activate page, which handles provider
        /// selection and the OAuth dance, then redirects back to our loopback.
        /// </summary>
        /// <param name="appUrl">Base URL of the Kaption web frontend (e.g. https://kaption.one).</param>
        /// <param name="deviceName">Human-readable device identifier (hostname).</param>
        public string BuildActivationUrl(string appUrl, string deviceName)
        {
            if (string.IsNullOrWhiteSpace(appUrl))
                throw new ArgumentException("appUrl required", nameof(appUrl));

            string baseUrl = appUrl.TrimEnd('/');
            string encodedState = Uri.EscapeDataString(_state);
            string encodedDevice = Uri.EscapeDataString(deviceName ?? "unknown-device");
            string encodedPort = _port.ToString(CultureInfo.InvariantCulture);
            return $"{baseUrl}/activate?state={encodedState}&port={encodedPort}&device={encodedDevice}";
        }

        /// <summary>
        /// Start the listener and wait for a successful callback. Returns the
        /// received token (the <c>device_activation_jwt</c> the Worker minted
        /// for this activation).
        ///
        /// Throws:
        ///   * <see cref="TimeoutException"/> — no request arrived within <paramref name="timeout"/>.
        ///   * <see cref="OperationCanceledException"/> — external cancellation.
        ///   * <see cref="LoopbackAuthException"/> — the browser posted an error
        ///     (e.g. <c>?error=access_denied</c>) or an invalid state.
        /// </summary>
        public async Task<string> WaitForTokenAsync(TimeSpan timeout, CancellationToken ct)
        {
            ThrowIfDisposed();

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new LoopbackAuthException(
                    "Could not start the local sign-in listener. Is another instance of Kaption running?", ex);
            }

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeout > TimeSpan.Zero && timeout < TimeSpan.MaxValue)
                    timeoutCts.CancelAfter(timeout);

                // Loop: accept requests, reject non-matching ones with 404/400,
                // only return once we see the right path + right state.
                while (true)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();

                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await GetContextWithCancellationAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Distinguish timeout from external cancellation.
                        if (ct.IsCancellationRequested)
                            throw;
                        throw new TimeoutException(
                            "The sign-in took too long. Please try again and complete sign-in within a few minutes.");
                    }

                    try
                    {
                        var result = HandleRequest(ctx);
                        if (result.IsComplete)
                        {
                            if (result.IsError)
                                throw new LoopbackAuthException(result.ErrorMessage);
                            return result.Token;
                        }
                        // Otherwise keep looping — we wrote a response already inside HandleRequest.
                    }
                    catch (LoopbackAuthException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Never crash the listener on a malformed request — log and continue.
                        Logger.Log.Warn($"Loopback listener ignored a malformed request: {ex.Message}");
                    }
                }
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Request handling
        // ───────────────────────────────────────────────────────────────────

        private readonly struct HandleResult
        {
            public bool IsComplete { get; }
            public bool IsError { get; }
            public string Token { get; }
            public string ErrorMessage { get; }

            private HandleResult(bool complete, bool error, string token, string message)
            {
                IsComplete = complete;
                IsError = error;
                Token = token;
                ErrorMessage = message;
            }

            public static HandleResult Continue() => new HandleResult(false, false, null, null);
            public static HandleResult Success(string token) => new HandleResult(true, false, token, null);
            public static HandleResult Error(string message) => new HandleResult(true, true, null, message);
        }

        private HandleResult HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteTextResponse(resp, 405, "Method not allowed.");
                return HandleResult.Continue();
            }

            string path = req.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path, CallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                // Unrelated request (favicon probe, etc.) — 404 and keep waiting.
                WriteTextResponse(resp, 404, "Not found.");
                return HandleResult.Continue();
            }

            var query = ParseQueryString(req.Url?.Query ?? string.Empty);
            string receivedState = query["state"];
            string token = query["token"];
            string error = query["error"];

            if (!string.IsNullOrEmpty(error))
            {
                string friendly = MapProviderError(error);
                WriteHtmlResponse(resp, 400, BuildErrorHtml(friendly));
                return HandleResult.Error(friendly);
            }

            if (!string.Equals(receivedState, _state, StringComparison.Ordinal))
            {
                // State mismatch — could be CSRF / stale callback. Reject and keep listening
                // in case the right callback arrives shortly after (unlikely but harmless).
                WriteHtmlResponse(resp, 400, BuildErrorHtml("The sign-in link's security check failed. Please try again."));
                Logger.Log.Warn("Loopback listener rejected callback with state mismatch.");
                return HandleResult.Continue();
            }

            if (string.IsNullOrEmpty(token))
            {
                WriteHtmlResponse(resp, 400, BuildErrorHtml("No activation token was received. Please try again."));
                return HandleResult.Error("Activation callback did not include a token.");
            }

            WriteHtmlResponse(resp, 200, BuildSuccessHtml());
            return HandleResult.Success(token);
        }

        // ───────────────────────────────────────────────────────────────────
        //  HTTP I/O
        // ───────────────────────────────────────────────────────────────────

        private static void WriteTextResponse(HttpListenerResponse resp, int statusCode, string body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                resp.StatusCode = statusCode;
                resp.ContentType = "text/plain; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                using (var output = resp.OutputStream)
                    output.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not write loopback response: {ex.Message}");
            }
        }

        private static void WriteHtmlResponse(HttpListenerResponse resp, int statusCode, string html)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                resp.StatusCode = statusCode;
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                using (var output = resp.OutputStream)
                    output.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not write loopback HTML response: {ex.Message}");
            }
        }

        private static string BuildSuccessHtml()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Kaption — signed in</title>
<style>
  html, body { margin: 0; padding: 0; height: 100%; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0F172A; color: #F1F5F9; }
  .wrap { display: flex; align-items: center; justify-content: center; height: 100%; }
  .card { max-width: 440px; padding: 32px 28px; text-align: center; background: #1E293B; border-radius: 12px; box-shadow: 0 20px 40px rgba(0,0,0,0.35); }
  h1 { margin: 0 0 12px; font-size: 22px; font-weight: 600; }
  p { margin: 0; color: #94A3B8; font-size: 14px; line-height: 1.55; }
  .check { width: 56px; height: 56px; margin: 0 auto 18px; border-radius: 50%; background: #059669; display: flex; align-items: center; justify-content: center; font-size: 30px; }
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""card"">
    <div class=""check"">&#10004;</div>
    <h1>You're signed in</h1>
    <p>You can close this tab and return to Kaption.</p>
  </div>
</div>
</body>
</html>";
        }

        // Replacement for HttpUtility.ParseQueryString — the System.Web dependency
        // was dropped in the net48 → net8 migration to avoid a NuGet-shim package.
        // Handles `?a=1&b=2&c=` and URL-decoding via System.Net.WebUtility.
        private static NameValueCollection ParseQueryString(string query)
        {
            var result = new NameValueCollection();
            if (string.IsNullOrEmpty(query)) return result;
            if (query.Length > 0 && query[0] == '?') query = query.Substring(1);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key, value;
                if (eq < 0)
                {
                    key = WebUtility.UrlDecode(pair);
                    value = string.Empty;
                }
                else
                {
                    key = WebUtility.UrlDecode(pair.Substring(0, eq));
                    value = WebUtility.UrlDecode(pair.Substring(eq + 1));
                }
                result.Add(key, value);
            }
            return result;
        }

        private static string BuildErrorHtml(string message)
        {
            string safe = WebUtility.HtmlEncode(message ?? "An error occurred during sign-in.");
            return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>Kaption — sign-in failed</title>
<style>
  html, body {{ margin: 0; padding: 0; height: 100%; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0F172A; color: #F1F5F9; }}
  .wrap {{ display: flex; align-items: center; justify-content: center; height: 100%; }}
  .card {{ max-width: 460px; padding: 32px 28px; text-align: center; background: #1E293B; border-radius: 12px; box-shadow: 0 20px 40px rgba(0,0,0,0.35); }}
  h1 {{ margin: 0 0 12px; font-size: 22px; font-weight: 600; }}
  p {{ margin: 0; color: #94A3B8; font-size: 14px; line-height: 1.55; }}
  .cross {{ width: 56px; height: 56px; margin: 0 auto 18px; border-radius: 50%; background: #DC2626; display: flex; align-items: center; justify-content: center; font-size: 30px; }}
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""card"">
    <div class=""cross"">&#10006;</div>
    <h1>Sign-in didn't go through</h1>
    <p>{safe}</p>
    <p style=""margin-top:14px;font-size:12px;color:#64748B;"">You can close this tab — Kaption will let you retry.</p>
  </div>
</div>
</body>
</html>";
        }

        private static string MapProviderError(string raw)
        {
            // Common OAuth error codes we want to surface in plain English.
            switch (raw?.ToLowerInvariant())
            {
                case "access_denied": return "You declined the sign-in request.";
                case "invalid_state": return "The sign-in link was invalid or expired. Please try again.";
                case "token_exchange_failed": return "The sign-in provider rejected our request. Please try again.";
                case "profile_fetch_failed": return "We couldn't read your profile from the sign-in provider.";
                case "profile_missing_email": return "Your account didn't share an email address, which Kaption requires.";
                default: return $"Sign-in failed: {raw}";
            }
        }

        // ───────────────────────────────────────────────────────────────────
        //  Infrastructure
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wrap <see cref="HttpListener.GetContextAsync"/> so it actually respects
        /// cancellation. The built-in method ignores tokens; we close the listener
        /// on cancel which makes GetContextAsync throw, and we convert that into
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        private async Task<HttpListenerContext> GetContextWithCancellationAsync(CancellationToken ct)
        {
            var getContext = _listener.GetContextAsync();
            using (ct.Register(() => { try { _listener.Stop(); } catch { /* may already be stopped */ } }))
            {
                try
                {
                    return await getContext.ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
            }
        }

        private static int PickFreeLoopbackPort()
        {
            // Bind a TcpListener to port 0, let the OS pick, read the port, release.
            // There's a tiny race window between release and HttpListener.Start() but
            // on a loopback ephemeral port it is effectively impossible for another
            // local process to grab that specific port in the gap.
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            try
            {
                return ((IPEndPoint)tcp.LocalEndpoint).Port;
            }
            finally
            {
                tcp.Stop();
            }
        }

        private static string GenerateStateNonce()
        {
            // 32 bytes = 256 bits of entropy, rendered as 64 hex chars.
            // Net8: RandomNumberGenerator.Fill replaces the obsolete
            // RNGCryptoServiceProvider (SYSLIB0023). Convert.ToHexString is
            // net5+, zero-alloc vs the manual StringBuilder loop.
            Span<byte> buf = stackalloc byte[32];
            RandomNumberGenerator.Fill(buf);
            return Convert.ToHexString(buf).ToLowerInvariant();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LoopbackAuthListener));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_listener.IsListening)
                    _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"LoopbackAuthListener dispose: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Thrown when the loopback flow fails in a way the user can understand
    /// (provider error, state mismatch with no recovery, etc.).
    /// </summary>
    public sealed class LoopbackAuthException : Exception
    {
        public LoopbackAuthException(string message) : base(message) { }
        public LoopbackAuthException(string message, Exception inner) : base(message, inner) { }
    }
}
