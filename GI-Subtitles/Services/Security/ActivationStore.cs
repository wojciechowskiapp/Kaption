using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Everything we persist about a completed activation.
    /// This is serialized to JSON, DPAPI-protected, and written to
    /// <c>%APPDATA%\Kaption\activation.dat</c>.
    /// </summary>
    public sealed class ActivationData
    {
        [JsonProperty("user_id")]
        public string UserId { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        /// <summary>Static base tier — permanent flags only. Use <see cref="EffectiveTier"/> to gate features.</summary>
        [JsonProperty("tier")]
        public string Tier { get; set; }

        /// <summary>
        /// Computed tier = max(base, highest active license grant).
        /// Source of truth for UI LABELS ONLY. Do NOT use this to gate
        /// valuable behavior that doesn't round-trip to the server —
        /// a Fiddler-class MITM can lie about it (the attestation on
        /// the wire is Ed25519-signed and verified in LicenseService,
        /// but a determined attacker with a patched binary can always
        /// defeat a purely-local check). All Pro-value work (file
        /// downloads, cloud calls) MUST be gated server-side against
        /// the real effective tier in D1.
        /// </summary>
        [JsonProperty("effective_tier")]
        public string EffectiveTier { get; set; }

        /// <summary>
        /// Unix seconds when the paid portion of the tier expires; null
        /// when no paid grant. Same caveat as <see cref="EffectiveTier"/>:
        /// display-only, not an authorization basis.
        /// </summary>
        [JsonProperty("paid_until")]
        public long? PaidUntilUnix { get; set; }

        /// <summary>Convenience: <see cref="PaidUntilUnix"/> as UTC DateTime, or null.</summary>
        [JsonIgnore]
        public DateTime? PaidUntilUtc =>
            PaidUntilUnix.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(PaidUntilUnix.Value).UtcDateTime
                : (DateTime?)null;

        /// <summary>True when the user has a currently-active paid license.</summary>
        [JsonIgnore]
        public bool HasPaidLicense =>
            PaidUntilUtc.HasValue && PaidUntilUtc.Value > DateTime.UtcNow;

        [JsonProperty("device_session_jwt")]
        public string DeviceSessionJwt { get; set; }

        /// <summary>
        /// Server-issued per-user key component. Used later (not yet) to derive
        /// encryption keys that bind ciphertexts to both the user AND the machine.
        /// </summary>
        [JsonProperty("user_key_component")]
        public byte[] UserKeyComponent { get; set; }

        /// <summary>
        /// 32 raw bytes of the global R2 distribution key. Used by
        /// <c>DistributionCipher</c> to decrypt `.gisub-dist` files on
        /// download before the desktop re-encrypts them machine-bound via
        /// <c>ServerKeyFileProtectionService</c>. Same value for every user;
        /// it's delivered inside the activation flow so scraping the R2 bucket
        /// (or the /download endpoint without auth) yields encrypted bytes
        /// with no way to derive the key.
        /// </summary>
        [JsonProperty("distribution_key")]
        public byte[] DistributionKey { get; set; }

        [JsonProperty("activation_id")]
        public string ActivationId { get; set; }

        /// <summary>Unix timestamp (seconds) when the server says this session expires.</summary>
        [JsonProperty("expires_at")]
        public long ExpiresAtUnix { get; set; }

        /// <summary>When we received this activation (local UTC clock).</summary>
        [JsonProperty("stored_at")]
        public DateTime StoredAtUtc { get; set; }

        /// <summary>Convenience: <see cref="ExpiresAtUnix"/> parsed as a UTC DateTime.</summary>
        [JsonIgnore]
        public DateTime ExpiresAtUtc => DateTimeOffset.FromUnixTimeSeconds(ExpiresAtUnix).UtcDateTime;

        /// <summary>True when we are past the server-declared expiry.</summary>
        [JsonIgnore]
        public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

        // ── File-protection key (server-issued) ──────────────────────────
        //
        // 32 random bytes that the desktop combines with the local machine
        // fingerprint via PBKDF2 to derive AES + HMAC keys for .gisub
        // translation packs. Issued by POST /api/app/file-protection-key
        // and persisted DPAPI-wrapped here.
        //
        // NEVER LOG these bytes. Use the `<redacted, len={len}>` pattern
        // when emitting log lines that mention the secret.

        /// <summary>
        /// Server-issued 32-byte device secret. Persisted DPAPI-wrapped
        /// transitively through <see cref="ActivationStore.Save"/>; never
        /// exists as plain JSON outside this object's lifetime.
        /// </summary>
        [JsonProperty("device_file_protection_secret")]
        public byte[] DeviceFileProtectionSecret { get; set; }

        /// <summary>Unix timestamp (ms) when the server issued the secret.</summary>
        [JsonProperty("device_file_protection_issued_at_ms")]
        public long? DeviceFileProtectionIssuedAtUnixMs { get; set; }

        /// <summary>Unix timestamp (ms) when the secret expires and must be re-fetched.</summary>
        [JsonProperty("device_file_protection_expires_at_ms")]
        public long? DeviceFileProtectionExpiresAtUnixMs { get; set; }

        /// <summary>
        /// Scheme version. <c>1</c> = AES-256-CBC + HMAC-SHA256 + PBKDF2-SHA256.
        /// <c>0</c> means the desktop has never fetched a secret (legacy).
        /// </summary>
        [JsonProperty("device_file_protection_scheme_version")]
        public int DeviceFileProtectionSchemeVersion { get; set; }

        /// <summary>PBKDF2 iteration count to use when deriving keys from this secret.</summary>
        [JsonProperty("device_file_protection_pbkdf2_iterations")]
        public int DeviceFileProtectionPbkdf2Iterations { get; set; }

        /// <summary>True when a non-expired server-issued secret is stored.</summary>
        [JsonIgnore]
        public bool HasDeviceFileProtectionSecret =>
            DeviceFileProtectionSecret != null
            && DeviceFileProtectionSecret.Length == 32
            && DeviceFileProtectionSchemeVersion >= GI_Subtitles.Services.Network.KaptionApiClient.FileProtectionSchemeVersion
            && DeviceFileProtectionPbkdf2Iterations >= GI_Subtitles.Services.Network.KaptionApiClient.MinPbkdf2Iterations
            && (!DeviceFileProtectionExpiresAtUnixMs.HasValue
                || DeviceTimeUnixMs() < DeviceFileProtectionExpiresAtUnixMs.Value);

        private static long DeviceTimeUnixMs() =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Persists the active license session under DPAPI (CurrentUser scope),
    /// so the file is bound to the Windows user account and can't be read by
    /// other local users or after the user profile is reset.
    ///
    /// Layout on disk:
    ///   * JSON payload → DPAPI-encrypted with app-specific entropy → base64 → file.
    ///
    /// Failure policy: every read returns <c>null</c> on any error (missing file,
    /// corrupted base64, DPAPI failure because the user migrated). The caller then
    /// prompts for a fresh sign-in. We never throw on <see cref="Load"/>.
    /// </summary>
    public static class ActivationStore
    {
        private const string FileName = "activation.dat";

        // Defense-in-depth entropy fed into DPAPI. Not secret — anyone with the
        // binary can extract it. Its job is to ensure that another Windows app
        // running under the same user account can't read our blob without also
        // duplicating this constant.
        private static readonly byte[] DpapiEntropy =
        {
            0x4B, 0x61, 0x70, 0x74, 0x69, 0x6F, 0x6E, 0x21,  // "Kaption!"
            0x61, 0x63, 0x74, 0x69, 0x76, 0x61, 0x74, 0x69,  // "activati"
            0x6F, 0x6E, 0x2D, 0x65, 0x6E, 0x74, 0x72, 0x6F,  // "on-entro"
            0x70, 0x79, 0x2D, 0x76, 0x31, 0x2E, 0x30, 0x30,  // "py-v1.00"
        };

        private static readonly object _ioLock = new object();

        private static string FilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kaption",
                FileName);

        /// <summary>
        /// Serialize + DPAPI-protect + write to disk. Overwrites any existing
        /// activation. Writes atomically via a .tmp sibling so a crash mid-write
        /// can never leave the real file in a half-encrypted state.
        /// </summary>
        public static void Save(ActivationData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            lock (_ioLock)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(data, Formatting.None);
                    byte[] plaintext = Encoding.UTF8.GetBytes(json);

                    byte[] protectedBlob = ProtectedData.Protect(
                        plaintext,
                        DpapiEntropy,
                        DataProtectionScope.CurrentUser);

                    string encoded = Convert.ToBase64String(protectedBlob);

                    string path = FilePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, encoded, Encoding.ASCII);

                    // Atomic replace — on Windows File.Move does not atomically replace
                    // an existing file, so delete first. Narrow window but good enough for
                    // a user-scoped config blob.
                    if (File.Exists(path))
                    {
                        try { File.Delete(path); }
                        catch (IOException ex)
                        {
                            Logger.Log.Warn($"ActivationStore could not delete existing file: {ex.Message}");
                        }
                    }
                    File.Move(tmp, path);

                    Logger.Log.Info(
                        $"Activation saved for user {data.Email} (tier={data.Tier}, expires {data.ExpiresAtUtc:u}, " +
                        $"hasFileProtSecret={data.HasDeviceFileProtectionSecret}, " +
                        $"secretLen={data.DeviceFileProtectionSecret?.Length ?? 0}).");
                }
                catch (CryptographicException ex)
                {
                    Logger.Log.Error($"DPAPI protect failed while saving activation: {ex.Message}");
                    throw;
                }
                catch (IOException ex)
                {
                    Logger.Log.Error($"IO error saving activation: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Read and decrypt the activation blob. Returns <c>null</c> on any failure
        /// (file missing, corrupted, DPAPI decrypt failed). Never throws.
        /// </summary>
        public static ActivationData Load()
        {
            lock (_ioLock)
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return null;

                try
                {
                    string encoded = File.ReadAllText(path, Encoding.ASCII);
                    if (string.IsNullOrWhiteSpace(encoded))
                        return null;

                    byte[] protectedBlob;
                    try { protectedBlob = Convert.FromBase64String(encoded.Trim()); }
                    catch (FormatException ex)
                    {
                        Logger.Log.Warn($"Activation file is not valid base64: {ex.Message}");
                        return null;
                    }

                    byte[] plaintext;
                    try
                    {
                        plaintext = ProtectedData.Unprotect(
                            protectedBlob,
                            DpapiEntropy,
                            DataProtectionScope.CurrentUser);
                    }
                    catch (CryptographicException ex)
                    {
                        // DPAPI refused — most likely user profile changed, or the file
                        // was copied from another user account. Treat as "not activated"
                        // rather than fatally throwing.
                        Logger.Log.Warn($"Could not decrypt activation blob (user profile changed?): {ex.Message}");
                        return null;
                    }

                    string json = Encoding.UTF8.GetString(plaintext);
                    var data = JsonConvert.DeserializeObject<ActivationData>(json);
                    if (data == null || string.IsNullOrEmpty(data.DeviceSessionJwt))
                    {
                        Logger.Log.Warn("Activation blob decoded but contained no session token.");
                        return null;
                    }
                    return data;
                }
                catch (IOException ex)
                {
                    Logger.Log.Warn($"Could not read activation file: {ex.Message}");
                    return null;
                }
                catch (JsonException ex)
                {
                    Logger.Log.Warn($"Activation JSON was malformed: {ex.Message}");
                    return null;
                }
                catch (Exception ex)
                {
                    // Last-resort: log and return null. This is a non-interactive
                    // read; we never want a corrupt blob to crash the app on startup.
                    Logger.Log.Warn($"Unexpected failure loading activation: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Remove the activation blob from disk. Safe to call if no blob exists.
        /// </summary>
        public static void Clear()
        {
            lock (_ioLock)
            {
                string path = FilePath;
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        Logger.Log.Info("Activation file cleared (sign-out).");
                    }
                }
                catch (IOException ex)
                {
                    Logger.Log.Warn($"Could not delete activation file: {ex.Message}");
                }
            }
        }
    }
}
