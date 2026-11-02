using System;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Fetches and stores this PC's file-protection secret (POST /api/app/file-protection-key). Used silently by the
    /// setup window's "Przygotowuję" step and, with its own window, by returning users whose key is missing.
    /// Three attempts (now, +1 s, +3 s), 10 s each; stops early on answers a retry cannot fix.
    /// </summary>
    public static class DeviceKeyBootstrap
    {
        /// <summary>Null when the key is stored; otherwise a message for the user.</summary>
        public static async Task<string> FetchAsync(LicenseService license, string deviceSessionJwt,
                                                   Func<string, string, string> L, CancellationToken ct = default)
        {
            if (license == null) throw new ArgumentNullException(nameof(license));
            int[] backoffMs = { 1000, 3000 };
            int maxAttempts = backoffMs.Length + 1;
            string lastError = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(10_000);
                    var resp = await new KaptionApiClient().FetchFileProtectionKeyAsync(deviceSessionJwt, cts.Token).ConfigureAwait(true);
                    if (resp == null || string.IsNullOrEmpty(resp.DeviceSecretB64))
                    {
                        lastError = L("shell_Protect_Err_Generic", "Check your internet connection and try again.");
                    }
                    else if (!Persist(license, resp))
                    {
                        Logger.Log.Error("DeviceKeyBootstrap: Persist returned false; the activation went missing during bootstrap.");
                        return L("shell_Protect_Err_Store", "Couldn't save the key on this PC. Restart Kaption and sign in again.");
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (UnauthorizedException ex)
                {
                    Logger.Log.Warn($"DeviceKeyBootstrap: unauthorised: {ex.Message}");
                    return L("shell_Protect_Err_Expired", "Your sign-in expired. Restart Kaption to sign in again.");
                }
                catch (FileProtectionKeyUnavailableException ex)
                {
                    Logger.Log.Warn($"DeviceKeyBootstrap: device not registered: {ex.Message}");
                    return L("shell_Protect_Err_Device", "This PC isn't registered. Restart Kaption to sign in again.");
                }
                catch (RateLimitedException ex)
                {
                    Logger.Log.Warn($"DeviceKeyBootstrap: rate limited (retry-after {ex.RetryAfter.TotalSeconds:N0}s)");
                    return string.Format(L("shell_Protect_Err_Busy_Format", "The server is busy. Try again in {0} s."),
                        Math.Max(1, (int)ex.RetryAfter.TotalSeconds));
                }
                catch (Exception ex)
                {
                    lastError = L("shell_Protect_Err_Generic", "Check your internet connection and try again.");
                    Logger.Log.Warn($"DeviceKeyBootstrap: attempt {attempt + 1}/{maxAttempts} failed ({ex.GetType().Name}: {ex.Message})");
                }
                if (attempt + 1 < maxAttempts) await Task.Delay(backoffMs[attempt], ct).ConfigureAwait(true);
            }
            return lastError;
        }

        private static bool Persist(LicenseService license, FileProtectionKeyResponse resp)
        {
            byte[] secret = KaptionApiClient.DecodeBase64Url(resp.DeviceSecretB64);
            bool ok = license.SetFileProtectionSecret(secret, ParseIso(resp.IssuedAt), ParseIso(resp.ExpiresAt), resp.Version, resp.PbkdfIterations);
            Logger.Log.Info($"DeviceKeyBootstrap: secret stored (len={secret.Length}, version={resp.Version}, iters={resp.PbkdfIterations}, persisted={ok}).");
            return ok;
        }

        internal static long? ParseIso(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime utc))
                return null;
            return new DateTimeOffset(utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime())
                .ToUnixTimeMilliseconds();
        }
    }
}
