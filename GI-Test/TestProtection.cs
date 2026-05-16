// ─────────────────────────────────────────────────────────────────────────────
//  TestProtection.cs
//  ---------------------------------------------------------------------------
//  Single-line factory that hands tests an IFileProtectionService backed by
//  the production ServerKeyFileProtectionService with a synthetic
//  ActivationData. Any test that previously did `new AesFileProtectionService()`
//  (the now-deleted legacy class) should call TestProtection.Create() instead.
//
//  The synthetic activation is deterministic — the secret bytes are computed
//  from a fixed seed so two runs of the same test on the same machine derive
//  identical AES + HMAC keys (machine fingerprint is the only varying input).
// ─────────────────────────────────────────────────────────────────────────────

using System;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;

namespace GI_Test
{
    internal static class TestProtection
    {
        /// <summary>
        /// Build an in-memory <see cref="ActivationData"/> with a usable
        /// 32-byte file-protection secret. Uses a deterministic seed so
        /// failures are reproducible across runs on the same machine.
        /// </summary>
        public static ActivationData FakeActivation(int seed = 0xA17C6)
        {
            byte[] secret = new byte[32];
            // Linear congruence — good enough for "non-zero, varied" bytes.
            int s = seed;
            for (int i = 0; i < secret.Length; i++)
            {
                s = unchecked(s * 1103515245 + 12345);
                secret[i] = (byte)((s >> 16) & 0xFF);
            }

            return new ActivationData
            {
                UserId = "test-user",
                Email = "test@example.invalid",
                ActivationId = "test-activation",
                DeviceSessionJwt = "fake-jwt-for-tests",
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                StoredAtUtc = DateTime.UtcNow,
                DeviceFileProtectionSecret = secret,
                DeviceFileProtectionIssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeviceFileProtectionExpiresAtUnixMs =
                    DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds(),
                DeviceFileProtectionSchemeVersion = KaptionApiClient.FileProtectionSchemeVersion,
                DeviceFileProtectionPbkdf2Iterations = 100_000,
            };
        }

        /// <summary>
        /// Convenience: hand a ready-to-use <see cref="IFileProtectionService"/>
        /// to a test. The service uses a fresh deterministic secret per call
        /// so two services in the same test produce identical keys.
        /// </summary>
        public static IFileProtectionService Create(int seed = 0xA17C6)
        {
            ActivationData act = FakeActivation(seed);
            return new ServerKeyFileProtectionService(() => act);
        }
    }
}
