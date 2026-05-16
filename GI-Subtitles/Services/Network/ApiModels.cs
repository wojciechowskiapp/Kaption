using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GI_Subtitles.Services.Network
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Request DTOs
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-component machine fingerprint sent to the activation endpoint.
    /// Each component is a lowercase-hex SHA-256 hash of a single hardware
    /// identifier (CPU ProcessorId, motherboard SerialNumber, disk SerialNumber).
    /// The server performs a soft-match ("2 of 3 components match an existing
    /// activation") so transient WMI failures don't orphan a user's license.
    /// </summary>
    public readonly struct MachineFingerprintPayload
    {
        [JsonProperty("cpu")]
        public string Cpu { get; }

        [JsonProperty("mobo")]
        public string Mobo { get; }

        [JsonProperty("disk")]
        public string Disk { get; }

        public MachineFingerprintPayload(string cpu, string mobo, string disk)
        {
            Cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
            Mobo = mobo ?? throw new ArgumentNullException(nameof(mobo));
            Disk = disk ?? throw new ArgumentNullException(nameof(disk));
        }
    }

    /// <summary>
    /// Body of POST /api/license/activate.
    /// JSON field names match the server contract in
    /// <c>backend/src/routes/license.ts</c> (session 21 review C-2 fix —
    /// previously used `device_activation_jwt` + `machine_fingerprint` which
    /// caused every activation to 400 at the server).
    /// </summary>
    public sealed class ActivateRequest
    {
        /// <summary>The 60s device activation JWT received on the loopback callback.</summary>
        [JsonProperty("activation_token")]
        public string ActivationToken { get; }

        /// <summary>Per-component fingerprint tuple (cpu, mobo, disk).</summary>
        [JsonProperty("fingerprint")]
        public MachineFingerprintPayload Fingerprint { get; }

        [JsonProperty("device_name")]
        public string DeviceName { get; }

        public ActivateRequest(string activationToken, MachineFingerprintPayload fingerprint, string deviceName)
        {
            ActivationToken = activationToken ?? throw new ArgumentNullException(nameof(activationToken));
            Fingerprint = fingerprint;
            DeviceName = deviceName ?? "unknown-device";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Response DTOs
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The signed payload returned as <c>activation_blob</c>. Server signs this
    /// object with its Ed25519 private key; desktop verifies offline using the
    /// bundled public key, then compares <see cref="MachineFingerprintHash"/>
    /// to its locally computed hash before trusting the payload.
    /// </summary>
    public sealed class ActivationBlobPayload
    {
        [JsonProperty("user_id", Order = 0)]
        public string UserId { get; private set; }

        [JsonProperty("activation_id", Order = 1)]
        public string ActivationId { get; private set; }

        [JsonProperty("machine_fingerprint_hash", Order = 2)]
        public string MachineFingerprintHash { get; private set; }

        [JsonProperty("user_key_component_b64", Order = 3)]
        public string UserKeyComponentB64 { get; private set; }

        /// <summary>
        /// Base64 of 32 raw bytes — the global R2 distribution key. Used to
        /// decrypt `.gisub-dist` files on download via DistributionCipher.
        /// Same value for every user; scoped by the activation flow so that
        /// scraping the download endpoint without a valid session gets
        /// encrypted bytes with no way to derive the key.
        /// </summary>
        [JsonProperty("distribution_key_b64", Order = 4)]
        public string DistributionKeyB64 { get; private set; }

        [JsonProperty("expires_at", Order = 5)]
        public long ExpiresAt { get; private set; }

        /// <summary>Decode the distribution key into raw bytes. Returns null if missing/invalid.</summary>
        public byte[] DecodeDistributionKey()
        {
            if (string.IsNullOrEmpty(DistributionKeyB64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(DistributionKeyB64);
                return bytes.Length == 32 ? bytes : null;
            }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>Public user shape nested in activation / /api/me responses.</summary>
    public sealed class UserPublic
    {
        [JsonProperty("id")] public string Id { get; private set; }
        [JsonProperty("email")] public string Email { get; private set; }
        [JsonProperty("name")] public string Name { get; private set; }
        [JsonProperty("avatar_url")] public string AvatarUrl { get; private set; }
        [JsonProperty("provider")] public string Provider { get; private set; }

        /// <summary>Static base tier — permanent flags like admin/beta. Server
        /// default for new accounts is <c>free_beta</c>.</summary>
        [JsonProperty("tier")] public string Tier { get; private set; }

        /// <summary>
        /// Computed tier = max(<see cref="Tier"/>, highest active license grant).
        /// This is what UI should gate features on. Added in migration 003 to
        /// support sellable license plans (pro-30d / pro-180d).
        /// </summary>
        [JsonProperty("effective_tier")] public string EffectiveTier { get; private set; }

        /// <summary>
        /// Unix-second expiry of the user's longest-running active license, or
        /// null when no grant is currently elevating the tier. UI renders this
        /// as "Pro (N days remaining)".
        /// </summary>
        [JsonProperty("paid_until")] public long? PaidUntil { get; private set; }

        [JsonProperty("created_at")] public long CreatedAt { get; private set; }
    }

    /// <summary>
    /// Successful response from POST /api/license/activate.
    /// Shape matches <c>backend/src/types.ts#ActivationResponse</c> (session 21
    /// review C-1 fix — fields were flat + misnamed before, leaving every
    /// property null after JSON deserialize).
    /// </summary>
    public sealed class ActivateResponse
    {
        [JsonProperty("activation_id")]
        public string ActivationId { get; private set; }

        /// <summary>Long-lived (30d) Bearer JWT for future /api/license/* calls.</summary>
        [JsonProperty("device_session_token")]
        public string DeviceSessionToken { get; private set; }

        [JsonProperty("device_session_expires_at")]
        public long DeviceSessionExpiresAt { get; private set; }

        /// <summary>The Ed25519-signed payload; verify before trusting.</summary>
        [JsonProperty("activation_blob")]
        public ActivationBlobPayload ActivationBlob { get; private set; }

        /// <summary>Base64 Ed25519 signature over JSON.stringify(activation_blob).</summary>
        [JsonProperty("activation_blob_signature")]
        public string ActivationBlobSignature { get; private set; }

        [JsonProperty("soft_expires_at")]
        public long SoftExpiresAt { get; private set; }

        [JsonProperty("hard_expires_at")]
        public long HardExpiresAt { get; private set; }

        [JsonProperty("user")]
        public UserPublic User { get; private set; }

        /// <summary>Decode the user key component into raw bytes. Returns null if missing/invalid.</summary>
        public byte[] DecodeUserKeyComponent()
        {
            var b64 = ActivationBlob?.UserKeyComponentB64;
            if (string.IsNullOrEmpty(b64)) return null;
            try { return Convert.FromBase64String(b64); }
            catch (FormatException) { return null; }
        }

        /// <summary>Decode the R2 distribution key into raw bytes. Returns null if absent/invalid.</summary>
        public byte[] DecodeDistributionKey() => ActivationBlob?.DecodeDistributionKey();
    }

    /// <summary>
    /// Body of POST /api/license/heartbeat. The heartbeat used to be a bodyless
    /// POST; session 26 added an optional <c>active_seconds</c> so the server can
    /// credit the user's referrer with bonus-days for actual usage. The field is
    /// only included in the wire payload when &gt;0 (see
    /// <see cref="_requestSerializerSettings"/> null-skip). The server caps the
    /// value at 3600 per heartbeat.
    /// </summary>
    public sealed class HeartbeatRequest
    {
        [JsonProperty("active_seconds", NullValueHandling = NullValueHandling.Ignore)]
        public int? ActiveSeconds { get; }

        public HeartbeatRequest(int? activeSeconds)
        {
            ActiveSeconds = activeSeconds;
        }
    }

    /// <summary>
    /// Response from POST /api/license/heartbeat.
    /// </summary>
    public sealed class HeartbeatResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; private set; }

        [JsonProperty("last_heartbeat")]
        public long LastHeartbeat { get; private set; }

        [JsonProperty("soft_expires_at")]
        public long SoftExpiresAt { get; private set; }

        [JsonProperty("hard_expires_at")]
        public long HardExpiresAt { get; private set; }

        /// <summary>
        /// Fresh effective tier (server recomputes on every heartbeat, so a
        /// license purchased mid-session lifts tier within the heartbeat
        /// interval without forcing a re-activation).
        /// </summary>
        [JsonProperty("effective_tier")]
        public string EffectiveTier { get; private set; }

        [JsonProperty("paid_until")]
        public long? PaidUntil { get; private set; }
    }

    /// <summary>
    /// Envelope the backend uses for list endpoints (`/api/license/files`,
    /// `/api/license/machines`). The Hono routes wrap their array in
    /// <c>c.json({ items })</c>, so on the wire the response looks like
    /// <c>{"items":[...]}</c> rather than a bare array. Deserialising the
    /// raw array directly throws `Cannot deserialize JSON object into List&lt;T&gt;`
    /// — see backend/src/routes/license.ts.
    /// </summary>
    internal sealed class ApiListEnvelope<T>
    {
        [JsonProperty("items")]
        public IReadOnlyList<T> Items { get; private set; }
    }

    /// <summary>Catalog entry from GET /api/license/files.</summary>
    public sealed class FileMetadata
    {
        [JsonProperty("id")]
        public string FileVersionId { get; private set; }

        [JsonProperty("game")]
        public string Game { get; private set; }

        [JsonProperty("language")]
        public string Language { get; private set; }

        [JsonProperty("version")]
        public string Version { get; private set; }

        [JsonProperty("file_size")]
        public long Size { get; private set; }

        [JsonProperty("file_sha256")]
        public string Sha256 { get; private set; }

        [JsonProperty("min_tier")]
        public string MinTier { get; private set; }

        [JsonProperty("released_at")]
        public long ReleasedAt { get; private set; }
    }

    /// <summary>
    /// Catalog entry from GET /api/license/gamedata. One row per (game, version) —
    /// no language field because gamedata bundles are language-agnostic
    /// (they carry DialogGraph / NpcNames / QuestInfo indexes that reference
    /// hash IDs, not localized text).
    /// </summary>
    public sealed class GamedataMetadata
    {
        [JsonProperty("id")]
        public string GamedataVersionId { get; private set; }

        [JsonProperty("game")]
        public string Game { get; private set; }

        [JsonProperty("version")]
        public string Version { get; private set; }

        [JsonProperty("file_size")]
        public long Size { get; private set; }

        [JsonProperty("file_sha256")]
        public string Sha256 { get; private set; }

        [JsonProperty("min_tier")]
        public string MinTier { get; private set; }

        [JsonProperty("released_at")]
        public long ReleasedAt { get; private set; }
    }

    /// <summary>
    /// One item returned by GET /api/announcements/active. The server
    /// filters by tier + time-window; the desktop just renders what it
    /// receives. See backend/src/routes/announcements.ts for the wire
    /// contract. `severity` is lowercase: info | warn | critical.
    /// </summary>
    public sealed class AnnouncementPublic
    {
        [JsonProperty("id")]
        public string Id { get; private set; }

        [JsonProperty("title")]
        public string Title { get; private set; }

        [JsonProperty("body")]
        public string Body { get; private set; }

        [JsonProperty("severity")]
        public string Severity { get; private set; }

        [JsonProperty("link_url")]
        public string LinkUrl { get; private set; }

        [JsonProperty("link_label")]
        public string LinkLabel { get; private set; }

        [JsonProperty("starts_at")]
        public long StartsAt { get; private set; }

        [JsonProperty("ends_at")]
        public long EndsAt { get; private set; }
    }

    /// <summary>Response from GET /api/app/version.</summary>
    public sealed class AppVersionInfo
    {
        [JsonProperty("version")]
        public string Version { get; private set; }

        [JsonProperty("download_url")]
        public string Url { get; private set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; private set; }

        [JsonProperty("release_notes_url")]
        public string ReleaseNotesUrl { get; private set; }

        [JsonProperty("min_supported_version")]
        public string MinSupportedVersion { get; private set; }

        [JsonProperty("published_at")]
        public long PublishedAt { get; private set; }
    }

    /// <summary>
    /// Shape of error responses from the API. Fields are optional — servers
    /// may return just <c>error</c>, or include <c>message</c> / <c>details</c>.
    /// </summary>
    public sealed class ApiError
    {
        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("details")]
        public Dictionary<string, object> Details { get; set; }

        /// <summary>
        /// Best-effort human-readable summary. Prefers <c>message</c>, falls
        /// back to <c>error</c>, then to <c>"unknown_error"</c>.
        /// </summary>
        public string Describe()
        {
            if (!string.IsNullOrWhiteSpace(Message))
                return Message;
            if (!string.IsNullOrWhiteSpace(Error))
                return Error;
            return "unknown_error";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Feedback
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Body of POST /api/feedback. Minimal on purpose — the server stores an
    /// email snapshot from the authenticated user plus the IP + UA, so the
    /// client only needs to ship the note itself (and opt-in metadata).
    /// </summary>
    public sealed class FeedbackRequest
    {
        [JsonProperty("message")]
        public string Message { get; }

        /// <summary>Always "desktop" from this client; the server accepts "web" too for future form reuse.</summary>
        [JsonProperty("client_kind")]
        public string ClientKind { get; }

        /// <summary>Application version string, e.g. <c>2.0.26040116</c>. Optional.</summary>
        [JsonProperty("client_version")]
        public string ClientVersion { get; }

        public FeedbackRequest(string message, string clientVersion)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            ClientKind = "desktop";
            ClientVersion = clientVersion;
        }
    }

    /// <summary>Reply from POST /api/feedback on success.</summary>
    public sealed class FeedbackResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Referrals (session 26)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rollup counters displayed on the Referrals dialog. Populated by the
    /// server — the desktop never computes these itself because the "Active"
    /// bucket needs data the client doesn't hold (e.g. the signed-up friend's
    /// active-seconds over time).
    /// </summary>
    public sealed class ReferralStats
    {
        /// <summary>Friends who followed the link and signed up.</summary>
        [JsonProperty("invited")]
        public int Invited { get; set; }

        /// <summary>Signed up but haven't cleared the activity bar yet.</summary>
        [JsonProperty("pending")]
        public int Pending { get; set; }

        /// <summary>Counted toward the 25 / 50 reward tiers.</summary>
        [JsonProperty("active")]
        public int Active { get; set; }

        /// <summary>Flagged as self-referral / fraud — excluded from totals.</summary>
        [JsonProperty("invalid")]
        public int Invalid { get; set; }

        /// <summary>
        /// Days of paid tier banked from referral conversion. Separate from
        /// the reward tiers; user can claim them as an extension instead of
        /// crystals, or stack them.
        /// </summary>
        [JsonProperty("bonus_days_banked")]
        public int BonusDaysBanked { get; set; }
    }

    /// <summary>
    /// One reward row from <c>/api/referrals/me</c>. Matches
    /// <c>backend/src/types.ts#ReferralReward</c>.
    /// </summary>
    public sealed class ReferralReward
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>"tier_25" or "tier_50". Other values reserved for future expansion.</summary>
        [JsonProperty("reward_type")]
        public string RewardType { get; set; }

        [JsonProperty("amount_crystals_nominal")]
        public int AmountCrystalsNominal { get; set; }

        [JsonProperty("amount_value_cents")]
        public int AmountValueCents { get; set; }

        /// <summary>
        /// One of: <c>pending</c>, <c>claim_submitted</c>, <c>approved</c>,
        /// <c>delivered</c>, <c>rejected</c>. Callers render action buttons
        /// based on this exact value — treat unknown strings as read-only.
        /// </summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("created_at")]
        public long CreatedAt { get; set; }

        [JsonProperty("claimed_at")]
        public long? ClaimedAt { get; set; }

        [JsonProperty("approved_at")]
        public long? ApprovedAt { get; set; }

        [JsonProperty("delivered_at")]
        public long? DeliveredAt { get; set; }

        /// <summary>
        /// Free-form message from the reviewing admin. Empty on the happy path;
        /// populated on rejection (reason) or approval (ETA / tracking info).
        /// </summary>
        [JsonProperty("admin_notes")]
        public string AdminNotes { get; set; }
    }

    /// <summary>
    /// One invitee in the referrer's own dashboard. The server masks the
    /// real email to <see cref="EmailHint"/> (format <c>ab***@g***.com</c>) —
    /// enough for the user to recognise a friend they explicitly shared the
    /// code with while avoiding a leak-worthy invitee directory. Counters
    /// (<see cref="RuntimeMinutes"/>, <see cref="ActiveDayCount"/>) drive
    /// the "45/120 min of play, 1/3 active days" progress hint rendered for
    /// pending rows so the referrer can see WHY a signup hasn't flipped
    /// active yet.
    /// </summary>
    public sealed class ReferralInvitee
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("email_hint")]
        public string EmailHint { get; set; }

        /// <summary>"pending" | "active" | "invalid"</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("created_at")]
        public long CreatedAt { get; set; }

        [JsonProperty("became_active_at")]
        public long? BecameActiveAt { get; set; }

        /// <summary>
        /// Unix seconds after which a pending referral can no longer flip
        /// to active regardless of subsequent play. Equal to
        /// <see cref="CreatedAt"/> + 30 days.
        /// </summary>
        [JsonProperty("activity_window_expires_at")]
        public long ActivityWindowExpiresAt { get; set; }

        [JsonProperty("runtime_minutes")]
        public int RuntimeMinutes { get; set; }

        [JsonProperty("active_day_count")]
        public int ActiveDayCount { get; set; }

        /// <summary>
        /// Coarse invalidation hint. Null on pending / active rows; set to
        /// <c>"flagged"</c> when the row is status='invalid'. The server
        /// deliberately collapses the specific reason so the referrer can't
        /// argue with it; admin panel has the detail.
        /// </summary>
        [JsonProperty("invalidation_hint")]
        public string InvalidationHint { get; set; }
    }

    /// <summary>
    /// Response shape for GET <c>/api/referrals/me</c> and
    /// POST <c>/api/referrals/ensure-code</c>. Both endpoints return the same
    /// envelope — the only difference is that ensure-code creates the code if
    /// the user doesn't have one yet.
    /// </summary>
    public sealed class ReferralMeResponse
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("share_url")]
        public string ShareUrl { get; set; }

        [JsonProperty("stats")]
        public ReferralStats Stats { get; set; }

        [JsonProperty("rewards")]
        public List<ReferralReward> Rewards { get; set; }

        [JsonProperty("invitees")]
        public List<ReferralInvitee> Invitees { get; set; }

        /// <summary>
        /// Mirrors <see cref="ReferralStats.BonusDaysBanked"/> at the top level
        /// for backwards compat — the server ships it in both places during
        /// the v1 rollout. Consumers should prefer <see cref="ReferralStats.BonusDaysBanked"/>
        /// and fall back to this when stats is null.
        /// </summary>
        [JsonProperty("bonus_days_banked")]
        public int BonusDaysBanked { get; set; }
    }

    /// <summary>
    /// Body of POST <c>/api/referrals/attribute</c>. The user submits a friend's
    /// code within 7 days of signup. The server rejects unknown codes (400),
    /// self-referrals (403) and duplicate attribution (409).
    /// </summary>
    public sealed class AttributeReferralRequest
    {
        [JsonProperty("code")]
        public string Code { get; }

        public AttributeReferralRequest(string code)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
        }
    }

    /// <summary>
    /// Body of POST <c>/api/referrals/rewards/{id}/claim</c>. The user picks a
    /// delivery method and provides the single piece of contact info the admin
    /// needs to fulfill the reward. Everything else (name, shipping address) is
    /// intentionally absent — crystals / gift codes don't need it and collecting
    /// extra PII just to store it is a privacy-policy liability.
    /// </summary>
    public sealed class ClaimReferralRewardRequest
    {
        /// <summary>"paypal" | "amazon_gc" | "hoyolab_uid"</summary>
        [JsonProperty("delivery_method")]
        public string DeliveryMethod { get; }

        /// <summary>
        /// PayPal / Amazon email, or HoYoLAB UID with region tag (e.g.
        /// "803912345 / NA"). Never logged client-side.
        /// </summary>
        [JsonProperty("contact_info")]
        public string ContactInfo { get; }

        public ClaimReferralRewardRequest(string deliveryMethod, string contactInfo)
        {
            DeliveryMethod = deliveryMethod ?? throw new ArgumentNullException(nameof(deliveryMethod));
            ContactInfo = contactInfo ?? throw new ArgumentNullException(nameof(contactInfo));
        }
    }

    /// <summary>
    /// Reply from POST <c>/api/referrals/rewards/{id}/claim</c> on success.
    /// The server returns the updated reward row so the UI can refresh without
    /// a round-trip to /me.
    /// </summary>
    public sealed class ClaimReferralRewardResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("reward")]
        public ReferralReward Reward { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  File-protection key (POST /api/app/file-protection-key)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Server response from <c>POST /api/app/file-protection-key</c>. The
    /// device combines <see cref="DeviceSecretB64"/> (32 random bytes,
    /// unpadded base64url) with the local machine fingerprint via PBKDF2 to
    /// derive AES + HMAC keys for <c>.gisub</c> translation packs cached
    /// under <c>%APPDATA%\Kaption</c>.
    ///
    /// Server may rotate <see cref="PbkdfIterations"/> over time; clients
    /// must clamp the returned value to <c>[50_000, 500_000]</c> before use
    /// to defend against a hostile or misconfigured backend asking for a
    /// multi-hour derivation.
    ///
    /// Clients must reject any <see cref="Version"/> they don't understand
    /// — newer schemes may swap AES-CBC for AES-GCM, change the PBKDF2 hash,
    /// etc. Strict version-checking is the migration path.
    /// </summary>
    public sealed class FileProtectionKeyResponse
    {
        [JsonProperty("device_secret_b64")]
        public string DeviceSecretB64 { get; set; }

        [JsonProperty("issued_at")]
        public string IssuedAt { get; set; }

        [JsonProperty("expires_at")]
        public string ExpiresAt { get; set; }

        /// <summary>Scheme version. Clients reject anything they don't recognise.</summary>
        [JsonProperty("version")]
        public int Version { get; set; }

        /// <summary>Human-informational string. Clients key off <see cref="Version"/> only.</summary>
        [JsonProperty("algorithm")]
        public string Algorithm { get; set; }

        [JsonProperty("pbkdf2_iterations")]
        public int PbkdfIterations { get; set; }
    }
}
