// ─────────────────────────────────────────────────────────────────────────────
//  TestProtection.cs
//  ---------------------------------------------------------------------------
//  Single-line factory that hands tests an IFileProtectionService backed by
//  the production ServerKeyFileProtectionService with a synthetic
//  ActivationData.
//
//  The synthetic activation is deterministic — the secret bytes are computed
//  from a fixed seed so two runs of the same test on the same machine derive
//  identical AES + HMAC keys (machine fingerprint is the only varying input).
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Reflection;
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

        /// <summary>
        /// Puts a <see cref="LicenseService"/> holding a fake activation into <c>App.LicenseService</c> until the
        /// returned scope is disposed, then puts back what was there. A window that builds its file protection while
        /// it is constructed (SettingsWindow calls <see cref="FileProtectionFactory.Create"/> in a field initializer)
        /// then never falls back to the activation on disk: a developer machine has the user's, a fresh CI runner has
        /// none, and the test must not depend on which one it runs on.
        ///
        /// The activation carries the file-protection secret but no session token, so nothing the window starts
        /// (its Translations scan) calls the API with it. The service is also marked disposed, so a revocation path
        /// (<see cref="LicenseService.ReportRemoteRevocation"/>) returns before it could clear the real activation file.
        /// </summary>
        public static IDisposable UseFakeLicense(int seed = 0xA17C6)
        {
            ActivationData activation = FakeActivation(seed);
            activation.DeviceSessionJwt = null;
            return new FakeLicenseScope(activation);
        }

        private sealed class FakeLicenseScope : IDisposable
        {
            private static readonly PropertyInfo AppLicense =
                typeof(GI_Subtitles.App).GetProperty(nameof(GI_Subtitles.App.LicenseService), BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMemberException(nameof(GI_Subtitles.App), nameof(GI_Subtitles.App.LicenseService));

            private static readonly FieldInfo Current = Field("_current");
            private static readonly FieldInfo Disposed = Field("_disposed");

            private readonly LicenseService _previous;
            private bool _restored;

            public FakeLicenseScope(ActivationData activation)
            {
                _previous = GI_Subtitles.App.LicenseService;
                // The public constructor only reads a stored activation (no network, no write); the fake replaces it.
                var fake = new LicenseService(new KaptionApiClient());
                Current.SetValue(fake, activation);
                Disposed.SetValue(fake, true);
                AppLicense.SetValue(null, fake);
            }

            public void Dispose()
            {
                if (_restored) return;
                _restored = true;
                AppLicense.SetValue(null, _previous);
            }

            private static FieldInfo Field(string name) =>
                typeof(LicenseService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(LicenseService), name);
        }
    }
}
