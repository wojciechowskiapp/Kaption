using System;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Pure helpers that turn an <see cref="ActivationData"/> snapshot into
    /// the short status strings the UI shows. Keeping these out of WPF
    /// code-behind so they're trivially unit-testable AND so multiple UI
    /// surfaces (Settings, system-tray tooltip, MainWindow status row, future
    /// "About" dialog) all render the exact same wording.
    ///
    /// Lifetime detection: the backend stores "lifetime" plans as
    /// <c>duration_days = 36500</c> (~100 years). Anything with PaidUntilUtc
    /// more than 20 years in the future renders as "Lifetime" rather than a
    /// numeric countdown — that threshold leaves headroom for renewals that
    /// stack but is small enough that no real renewable plan would ever
    /// cross it.
    /// </summary>
    public static class LicenseStatusFormatter
    {
        private static readonly TimeSpan LifetimeThreshold = TimeSpan.FromDays(365 * 20);

        /// <summary>
        /// Short single-line label suitable for a status row, system tray
        /// tooltip, or settings header. Examples:
        ///   "Free"
        ///   "Free beta"
        ///   "Pro — 27 days left"
        ///   "Pro — expires today"
        ///   "Pro — Lifetime"
        ///   "Pro (expired)"
        ///   "Signed out"
        /// </summary>
        public static string ShortLabel(ActivationData activation)
        {
            if (activation == null) return "Signed out";

            string tier = activation.EffectiveTier ?? activation.Tier ?? "free";

            // Non-paid tiers — render the tier name with a friendlier word.
            if (!IsPaid(tier))
            {
                if (string.Equals(tier, "free_beta", StringComparison.OrdinalIgnoreCase))
                    return "Free beta";
                if (string.Equals(tier, "admin", StringComparison.OrdinalIgnoreCase))
                    return "Admin";
                return "Free";
            }

            // Paid tier — figure out how much time is left.
            DateTime? paidUntil = activation.PaidUntilUtc;
            if (!paidUntil.HasValue)
            {
                // Paid tier from a non-license source (e.g. base tier set to
                // 'paid' on the user row itself). No expiry to render.
                return "Pro";
            }

            TimeSpan remaining = paidUntil.Value - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return "Pro (expired)";

            if (remaining >= LifetimeThreshold)
                return "Pro — Lifetime";

            int daysLeft = (int)Math.Ceiling(remaining.TotalDays);
            if (daysLeft <= 1)
                return "Pro — expires today";
            if (daysLeft == 1)
                return "Pro — 1 day left";
            return $"Pro — {daysLeft} days left";
        }

        /// <summary>
        /// Multi-line tooltip with email + tier + expiry. Suitable for the
        /// system tray icon's tooltip or a hover-card. Returns "" when the
        /// activation is null so the consumer can hide the surface entirely.
        /// </summary>
        public static string Tooltip(ActivationData activation)
        {
            if (activation == null) return string.Empty;
            string label = ShortLabel(activation);
            string email = activation.Email ?? "(no email)";

            DateTime? paidUntil = activation.PaidUntilUtc;
            if (paidUntil.HasValue && (paidUntil.Value - DateTime.UtcNow) < LifetimeThreshold)
            {
                // Show explicit date for non-lifetime grants — no ambiguity
                // about when the user needs to renew.
                return $"Kaption — {label}\n{email}\nExpires {paidUntil.Value.ToLocalTime():yyyy-MM-dd}";
            }
            return $"Kaption — {label}\n{email}";
        }

        /// <summary>
        /// True when the effective tier on this activation grants paid
        /// features. Display-only — this is NOT an authorization check. All
        /// real authorization happens server-side at every protected endpoint.
        /// </summary>
        public static bool IsPaid(string tier)
        {
            if (string.IsNullOrEmpty(tier)) return false;
            return string.Equals(tier, "paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tier, "admin", StringComparison.OrdinalIgnoreCase);
        }
    }
}
