using System;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Single entry point for constructing the
    /// <see cref="IFileProtectionService"/> for the current process.
    ///
    /// As of the source-available release there is exactly one implementation
    /// — <see cref="ServerKeyFileProtectionService"/>, which derives keys from
    /// a per-device secret issued by <c>POST /api/app/file-protection-key</c>
    /// combined with the local machine fingerprint. Nothing in the desktop
    /// source carries a usable secret.
    ///
    /// Pre-condition: the foreground bootstrap in
    /// <c>App.OnStartup.EnsureFileProtectionSecret</c> MUST have run and
    /// stored a secret on the live <see cref="LicenseService"/>. The factory
    /// throws if it hasn't — that's a programmer error, not a user-recoverable
    /// state.
    ///
    /// Source of truth: the in-memory <see cref="LicenseService.CurrentActivation"/>
    /// rather than a fresh <see cref="ActivationStore.Load"/>. Both end up
    /// reflecting the same on-disk file in the steady state, but the
    /// in-memory copy is robust to transient disk-save failures (the
    /// dialog path returns success when the in-memory write succeeds even
    /// if the disk save throws — see <c>LicenseService.SetFileProtectionSecret</c>).
    /// Falls back to a disk read only when no <see cref="LicenseService"/>
    /// instance exists (test code that bypasses <c>App</c>).
    /// </summary>
    public static class FileProtectionFactory
    {
        /// <summary>
        /// Build the protection service. Throws if no server-issued secret is
        /// available — the foreground bootstrap is responsible for
        /// guaranteeing one exists before this is called.
        /// </summary>
        public static IFileProtectionService Create()
        {
            ActivationData activation = ResolveActivation();
            if (activation == null || !activation.HasDeviceFileProtectionSecret)
            {
                throw new InvalidOperationException(
                    "FileProtectionFactory.Create called before a per-device secret "
                    + "was provisioned. Foreground bootstrap (App.OnStartup) must run "
                    + "and succeed before any file-protection consumer is constructed. "
                    + "If you see this in production it means the bootstrap silently "
                    + "skipped — investigate the StartupStatus event.");
            }

            return new ServerKeyFileProtectionService(ResolveActivation);
        }

        /// <summary>
        /// In-memory first, disk fallback. Reads the live
        /// <see cref="App.LicenseService"/> when one exists; falls back to
        /// <see cref="ActivationStore.Load"/> only for code paths that run
        /// without an <c>App</c> instance (test fixtures, the rare admin
        /// CLI). Both routes ultimately observe the same activation; the
        /// indirection just shields us from the in-memory/disk split when
        /// the bootstrap dialog persists in-memory but the disk write
        /// throws.
        /// </summary>
        private static ActivationData ResolveActivation()
        {
            var live = GI_Subtitles.App.LicenseService?.CurrentActivation;
            if (live != null) return live;
            return ActivationStore.Load();
        }
    }
}
