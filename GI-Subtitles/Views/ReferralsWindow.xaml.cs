using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Desktop view over the Kaption referrals program. Fetches the user's
    /// code, stats and rewards from <c>/api/referrals/ensure-code</c>, lets
    /// them copy their share link, claim rewards (inline panel — no
    /// sub-windows), and optionally attribute a friend's code within the 7-day
    /// signup window.
    ///
    /// Mirrors <see cref="SendFeedbackWindow"/> on auth (Bearer JWT from
    /// <see cref="App.LicenseService"/>), error surfacing (typed exception
    /// branches → inline human-readable copy), and dispatcher hygiene (every
    /// async continuation marshals back through <c>Dispatcher</c> before
    /// touching UI).
    ///
    /// Threshold values are mirrored from the landing site
    /// (landing/src/data/referral-tiers.mjs) and the backend
    /// (backend/src/lib/referrals.ts: TIER_25_THRESHOLD / TIER_50_THRESHOLD).
    /// If you bump them in either of those, update <see cref="TierLowerFriends"/>
    /// and <see cref="TierUpperFriends"/> below to match.
    ///
    /// Security notes:
    ///   - Delivery contact info (email / UID) is never logged to debug.
    ///   - Client-side format validation gates every submit so we don't
    ///     waste a round trip on an obvious typo.
    ///   - Clipboard writes are wrapped in try/catch because WPF's
    ///     Clipboard.SetText throws on contention (other app holding the
    ///     clipboard lock).
    /// </summary>
    public partial class ReferralsWindow : Window
    {
        /// <summary>
        /// Lower-tier crystal milestone threshold — # of active friends
        /// required to unlock the 1,980-crystal reward. Mirror of
        /// landing/src/data/referral-tiers.mjs::TIER_LOWER_FRIENDS and
        /// backend/src/lib/referrals.ts::TIER_25_THRESHOLD.
        /// </summary>
        private const int TierLowerFriends = 15;

        /// <summary>
        /// Upper-tier crystal milestone threshold — # of active friends
        /// required to unlock the 3,280-crystal reward. Mirror of
        /// landing/src/data/referral-tiers.mjs::TIER_UPPER_FRIENDS and
        /// backend/src/lib/referrals.ts::TIER_50_THRESHOLD.
        /// </summary>
        private const int TierUpperFriends = 40;

        /// <summary>
        /// The <c>utm</c> tag added when opening /referrals from the desktop
        /// so the landing page can attribute sign-ups that originated here
        /// separately from organic browser traffic. Also serves as a breadcrumb
        /// in admin analytics.
        /// </summary>
        private const string ReferralsWebUrl = "https://kaption.one/referrals?utm=desktop";

        /// <summary>
        /// Reasonable per-request timeout. Referrals endpoints are tiny JSON
        /// round-trips; 20 s is generous for even flaky residential networks.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        // ── Client-side validation regex ────────────────────────────────────
        //
        // Deliberately loose — we want "looks like an email" / "looks like a UID"
        // rather than a full RFC-compliant check. The server does the strict
        // validation; we're catching obvious slips before a round trip.
        //
        // Net8: RegexOptions.Compiled → [GeneratedRegex] source generator.
        // Cold (user-input path), but converted for AOT-readiness + consistency
        // with the VoiceContentHelper + AnswerTranslationService patterns.
        [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant)]
        private static partial Regex EmailRegex();

        [GeneratedRegex(@"^\d{9,12}$", RegexOptions.CultureInvariant)]
        private static partial Regex HoyolabUidRegex();

        [GeneratedRegex(@"^[A-Z0-9]{3,12}(-[A-Z0-9]{3,12})?$", RegexOptions.CultureInvariant)]
        private static partial Regex ReferralCodeRegex();

        private readonly KaptionApiClient _api;

        /// <summary>Latest fetched envelope; null until the initial load completes.</summary>
        private ReferralMeResponse _current;

        /// <summary>ID of the reward currently expanded in the claim panel, or null.</summary>
        private string _activeClaimRewardId;

        /// <summary>Latched while any outbound request is in flight; prevents double-click storms.</summary>
        private bool _submittingClaim;
        private bool _submittingAttribution;
        private bool _loading;

        private static string L(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key) as string ?? fallback;
        }

        private static string LF(string key, string fallback, params object[] args)
        {
            return string.Format(L(key, fallback), args);
        }

        public ReferralsWindow()
        {
            InitializeComponent();
            _api = new KaptionApiClient();

            Loaded += (_, __) =>
            {
                // Kick the initial load as soon as the window is ready. Running
                // on Loaded (rather than the constructor) means the progress bar
                // actually renders before the request fires.
                _ = LoadAsync();
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  LOADING
        // ══════════════════════════════════════════════════════════════════

        private async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            SetViewState(ViewState.Loading);

            string jwt = App.LicenseService?.CurrentActivation?.DeviceSessionJwt;
            if (string.IsNullOrEmpty(jwt))
            {
                ShowError(L("Referrals_LoadError_NotSignedIn", "You need to be signed in to view referrals. Close this window, sign in, and try again."));
                _loading = false;
                return;
            }

            try
            {
                using (var cts = new CancellationTokenSource(RequestTimeout))
                {
                    // EnsureReferralCodeAsync is idempotent server-side — safe
                    // to call on every open. Returns the same code for repeat
                    // invocations, or creates one on first call.
                    var resp = await _api.EnsureReferralCodeAsync(jwt, cts.Token)
                        .ConfigureAwait(true);

                    _current = resp ?? new ReferralMeResponse();
                    PopulateFromResponse(_current);
                    SetViewState(ViewState.Content);
                }
            }
            catch (UnauthorizedException)
            {
                ShowError(L("Referrals_LoadError_Expired", "Your Kaption session has expired. Close this window, sign in again, and retry."));
            }
            catch (ForbiddenException ex)
            {
                ShowError(ex.Message ?? L("Referrals_LoadError_Forbidden", "Your account doesn't currently have access to referrals."));
            }
            catch (ApiValidationException ex)
            {
                ShowError(ex.Error?.Describe() ?? L("Referrals_LoadError_Rejected", "The server rejected the request."));
            }
            catch (ApiUnavailableException)
            {
                ShowError(L("Referrals_LoadError_Unreachable", "We couldn't reach Kaption's servers. Check your connection and try again."));
            }
            catch (OperationCanceledException)
            {
                ShowError(L("Referrals_LoadError_Timeout", "The request took too long. Please try again."));
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"ReferralsWindow load failed: {ex}");
                ShowError(L("Referrals_LoadError_Generic", "Something went wrong while loading your referrals. Please try again."));
            }
            finally
            {
                _loading = false;
            }
        }

        private void RetryLoad_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadAsync();
        }

        /// <summary>
        /// Apply a <see cref="ReferralMeResponse"/> to every bound UI element.
        /// Safe to call multiple times; replaces whatever's on screen.
        /// </summary>
        private void PopulateFromResponse(ReferralMeResponse resp)
        {
            // Code + share URL. Empty code means the server is running a
            // version that issues codes on-demand; render a placeholder
            // instead of a blank so the user can see the window loaded.
            CodeText.Text = string.IsNullOrWhiteSpace(resp.Code) ? "----" : resp.Code;
            ShareUrlText.Text = resp.ShareUrl ?? "";

            int invited = resp.Stats?.Invited ?? 0;
            int active = resp.Stats?.Active ?? 0;
            int pending = resp.Stats?.Pending ?? 0;
            // Prefer the stats-scoped field; the top-level mirror is legacy.
            int bonusDays = resp.Stats?.BonusDaysBanked ?? resp.BonusDaysBanked;

            StatInvitedText.Text = invited.ToString("N0");
            StatActiveText.Text = active.ToString("N0");
            StatBonusDaysText.Text = bonusDays.ToString("N0");
            StatPendingText.Text = pending.ToString("N0");

            Tier1Progress.Value = Math.Min(active, TierLowerFriends);
            Tier1ProgressText.Text = $"{Math.Min(active, TierLowerFriends)} / {TierLowerFriends}";
            Tier2Progress.Value = Math.Min(active, TierUpperFriends);
            Tier2ProgressText.Text = $"{Math.Min(active, TierUpperFriends)} / {TierUpperFriends}";

            PopulateInvitees(resp.Invitees);
            PopulateRewards(resp.Rewards);
        }

        // ══════════════════════════════════════════════════════════════════
        //  INVITEES
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Activity thresholds mirrored from
        /// <c>backend/src/lib/referrals.ts::ACTIVITY_MINUTES_REQUIRED</c> and
        /// <c>ACTIVITY_DAYS_REQUIRED</c>. Shown inline on pending rows so the
        /// referrer sees "45 / 120 min · 1 / 3 days" and understands exactly
        /// what's missing. Bump these together with the server constants if
        /// the bar ever changes.
        /// </summary>
        private const int ActivityMinutesRequired = 120;
        private const int ActivityDaysRequired = 3;

        /// <summary>
        /// Render the invitees list. Pending rows surface a progress hint
        /// (minutes + days) because that's the single most common support
        /// question: "I invited my friend, why do I have 0 active?".
        /// </summary>
        private void PopulateInvitees(IList<ReferralInvitee> invitees)
        {
            InviteesList.Items.Clear();

            if (invitees == null || invitees.Count == 0)
            {
                InviteesCard.Visibility = Visibility.Collapsed;
                InviteesSubtitleText.Text = string.Empty;
                return;
            }

            InviteesCard.Visibility = Visibility.Visible;

            int pendingCount = 0, activeCount = 0, invalidCount = 0;
            foreach (var invitee in invitees)
            {
                switch ((invitee?.Status ?? "").ToLowerInvariant())
                {
                    case "active":  activeCount++; break;
                    case "invalid": invalidCount++; break;
                    default:        pendingCount++; break;
                }
            }
            InviteesSubtitleText.Text = FormatInviteesSubtitle(
                pending: pendingCount,
                active: activeCount,
                invalid: invalidCount);

            // Server already orders pending → active → invalid, created_at DESC.
            // We trust the server order rather than re-sorting client-side so
            // both surfaces (web + desktop) agree.
            foreach (var invitee in invitees)
            {
                if (invitee == null) continue;
                InviteesList.Items.Add(BuildInviteeRow(invitee));
            }
        }

        private static string FormatInviteesSubtitle(int pending, int active, int invalid)
        {
            var parts = new List<string>(3);
            if (pending > 0)  parts.Add(LF("Referrals_Invitee_Summary_Pending_Format", "{0} pending", pending));
            if (active > 0)   parts.Add(LF("Referrals_Invitee_Summary_Active_Format", "{0} active", active));
            if (invalid > 0)  parts.Add(LF("Referrals_Invitee_Summary_Flagged_Format", "{0} flagged", invalid));
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }

        private Border BuildInviteeRow(ReferralInvitee invitee)
        {
            string status = (invitee.Status ?? "").ToLowerInvariant();

            var row = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status pill on the left.
            var pill = new Border
            {
                Background = ResolveInviteeStatusBrush(status),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            pill.Child = new System.Windows.Controls.TextBlock
            {
                Text = DescribeInviteeStatus(status),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
            };
            Grid.SetColumn(pill, 0);
            grid.Children.Add(pill);

            // Email hint + signup timing.
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(invitee.EmailHint)
                    ? L("Referrals_Invitee_FriendFallback", "friend")
                    : invitee.EmailHint,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("TextBrush"),
                FontFamily = new FontFamily("Consolas"),
            });
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = BuildSignupLine(invitee, status),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(stack, 2);
            grid.Children.Add(stack);

            // Right-aligned progress hint for pending rows. Active / invalid
            // rows get a small sub-label instead (handled inside BuildSignupLine).
            if (status == "pending")
            {
                var progress = new System.Windows.Controls.TextBlock
                {
                    Text = BuildPendingProgressText(invitee),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                };
                Grid.SetColumn(progress, 3);
                grid.Children.Add(progress);
            }

            row.Child = grid;
            return row;
        }

        private Brush ResolveInviteeStatusBrush(string status)
        {
            switch (status)
            {
                case "active":
                    return (Brush)FindResource("SuccessBrush");
                case "invalid":
                    return (Brush)FindResource("ErrorBrush");
                default: // pending
                    return new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
            }
        }

        private static string DescribeInviteeStatus(string status)
        {
            switch (status)
            {
                case "active":  return L("Referrals_Invitee_Active", "ACTIVE");
                case "invalid": return L("Referrals_Invitee_Flagged", "FLAGGED");
                default:        return L("Referrals_Invitee_Pending", "PENDING");
            }
        }

        /// <summary>
        /// Second line under the email hint — a friendly rendering of signup
        /// date + lifecycle state ("Signed up 3 days ago · window closes in
        /// 27d", "Active since yesterday", "Flagged — admin reviewing"). We
        /// compute ages relative to the local clock; the backend ships unix
        /// seconds in UTC so the client doesn't need a timezone.
        /// </summary>
        private static string BuildSignupLine(ReferralInvitee invitee, string status)
        {
            var signedUp = DateTimeOffset.FromUnixTimeSeconds(invitee.CreatedAt);
            string signedUpRel = RelativePast(signedUp);

            if (status == "active")
            {
                if (invitee.BecameActiveAt.HasValue)
                {
                    var became = DateTimeOffset.FromUnixTimeSeconds(invitee.BecameActiveAt.Value);
                    return LF("Referrals_Invitee_ActiveSince_Format", "Active since {0} · signed up {1}", RelativePast(became), signedUpRel);
                }
                return LF("Referrals_Invitee_ActiveNoDate_Format", "Active · signed up {0}", signedUpRel);
            }

            if (status == "invalid")
            {
                return LF("Referrals_Invitee_FlaggedLine_Format", "Flagged for manual review · signed up {0}", signedUpRel);
            }

            // Pending — surface the 30-day window expiry so the user knows
            // the clock is ticking.
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long remaining = invitee.ActivityWindowExpiresAt - nowSec;
            if (remaining <= 0)
            {
                return LF("Referrals_Invitee_WindowClosed_Format", "Signed up {0} · activity window closed", signedUpRel);
            }
            int daysLeft = (int)Math.Max(1, Math.Ceiling(remaining / 86400.0));
            return LF("Referrals_Invitee_DaysLeft_Format", "Signed up {0} · {1}d left to qualify", signedUpRel, daysLeft);
        }

        /// <summary>
        /// Right-hand progress hint for pending rows. Two fractions, tab-aligned
        /// so the column lines up vertically across rows of different widths.
        /// </summary>
        private static string BuildPendingProgressText(ReferralInvitee invitee)
        {
            int mins = Math.Min(invitee.RuntimeMinutes, ActivityMinutesRequired);
            int days = Math.Min(invitee.ActiveDayCount, ActivityDaysRequired);
            return LF("Referrals_Invitee_Progress_Format", "{0} / {1} min\n{2} / {3} days", mins, ActivityMinutesRequired, days, ActivityDaysRequired);
        }

        /// <summary>
        /// Human-friendly "N {unit} ago". Falls through to absolute date past
        /// ~30 days so we don't render "Signed up 420 days ago" — that's
        /// useless and "2026-04-01" is more scannable.
        /// </summary>
        private static string RelativePast(DateTimeOffset when)
        {
            var now = DateTimeOffset.UtcNow;
            var delta = now - when;
            if (delta.TotalSeconds < 60) return L("Referrals_Relative_JustNow", "just now");
            if (delta.TotalMinutes < 60)
            {
                int m = (int)Math.Max(1, Math.Round(delta.TotalMinutes));
                return LF("Referrals_Relative_MinutesAgo_Format", "{0} min ago", m);
            }
            if (delta.TotalHours < 24)
            {
                int h = (int)Math.Max(1, Math.Round(delta.TotalHours));
                return LF("Referrals_Relative_HoursAgo_Format", "{0}h ago", h);
            }
            if (delta.TotalDays < 2) return L("Referrals_Relative_Yesterday", "yesterday");
            if (delta.TotalDays < 30)
            {
                int d = (int)Math.Floor(delta.TotalDays);
                return LF("Referrals_Relative_DaysAgo_Format", "{0} days ago", d);
            }
            return when.ToLocalTime().ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// Render the rewards list. We build fully-styled rows in code rather
        /// than through an ItemTemplate so the per-row button wiring stays
        /// close to the click handler — easier to audit and evolve.
        /// </summary>
        private void PopulateRewards(IList<ReferralReward> rewards)
        {
            RewardsList.Items.Clear();

            if (rewards == null || rewards.Count == 0)
            {
                RewardsCard.Visibility = Visibility.Collapsed;
                return;
            }

            RewardsCard.Visibility = Visibility.Visible;

            foreach (var reward in rewards.OrderByDescending(r => r.CreatedAt))
            {
                RewardsList.Items.Add(BuildRewardRow(reward));
            }
        }

        private Border BuildRewardRow(ReferralReward reward)
        {
            // One card per reward. Grid: [badge | title+status | action button].
            var row = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Crystal badge (e.g. "1,980" or "3,280").
            var badge = new Border
            {
                Background = ResolveBadgeBrush(reward),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = new System.Windows.Controls.TextBlock
            {
                Text = reward.AmountCrystalsNominal.ToString("N0"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
            };
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            // Title + status line.
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = DescribeRewardTitle(reward),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
            });

            var statusText = new System.Windows.Controls.TextBlock
            {
                Text = DescribeRewardStatus(reward),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = ResolveStatusBrush(reward),
                TextWrapping = TextWrapping.Wrap,
            };
            titleStack.Children.Add(statusText);

            // Admin notes are the only place rejection reason is carried.
            if (!string.IsNullOrWhiteSpace(reward.AdminNotes))
            {
                titleStack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = reward.AdminNotes,
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            Grid.SetColumn(titleStack, 2);
            grid.Children.Add(titleStack);

            // Action button — varies by status.
            var action = BuildRewardActionButton(reward);
            if (action != null)
            {
                Grid.SetColumn(action, 3);
                grid.Children.Add(action);
            }

            row.Child = grid;
            return row;
        }

        private Brush ResolveBadgeBrush(ReferralReward reward)
        {
            // 1980 → accent, 3280 → success; future tiers → neutral.
            if (reward.AmountCrystalsNominal >= 3000)
                return (Brush)FindResource("SuccessBrush");
            return (Brush)FindResource("AccentBrush");
        }

        private Brush ResolveStatusBrush(ReferralReward reward)
        {
            switch ((reward.Status ?? "").ToLowerInvariant())
            {
                case "delivered":
                case "approved":
                    return (Brush)FindResource("SuccessBrush");
                case "rejected":
                    return (Brush)FindResource("ErrorBrush");
                default:
                    return (Brush)FindResource("TextSecondaryBrush");
            }
        }

        private static string DescribeRewardTitle(ReferralReward reward)
        {
            if (reward.AmountCrystalsNominal > 0)
                return LF("Referrals_Reward_CrystalsTitle_Format", "{0} primogems", reward.AmountCrystalsNominal.ToString("N0"));
            if (reward.AmountValueCents > 0)
                return LF("Referrals_Reward_CashTitle_Format", "{0}$", (reward.AmountValueCents / 100m).ToString("0.00"));
            return reward.RewardType ?? L("Referrals_Reward_Generic", "Reward");
        }

        private static string DescribeRewardStatus(ReferralReward reward)
        {
            switch ((reward.Status ?? "").ToLowerInvariant())
            {
                case "pending":           return L("Referrals_Reward_Status_Pending", "Ready to claim");
                case "claim_submitted":   return L("Referrals_Reward_Status_Submitted", "Waiting for review");
                case "approved":          return L("Referrals_Reward_Status_Approved", "Approved — delivering soon");
                case "delivered":         return L("Referrals_Reward_Status_Delivered", "Delivered ✓");
                case "rejected":          return L("Referrals_Reward_Status_Rejected", "Rejected");
                default:                  return reward.Status ?? "";
            }
        }

        /// <summary>
        /// Pick the right button per reward state. <c>null</c> means no
        /// button (e.g. delivered — nothing to do).
        /// </summary>
        private Button BuildRewardActionButton(ReferralReward reward)
        {
            string status = (reward.Status ?? "").ToLowerInvariant();
            if (status == "pending")
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("ModernButton"),
                    Content = L("Referrals_Reward_Action_ClaimNow", "Claim now"),
                    MinWidth = 110,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = reward.Id,
                };
                btn.Click += (_, __) => OpenClaimPanelFor(reward);
                return btn;
            }

            // claim_submitted / approved / delivered / rejected — just render a
            // disabled "pill" so the user can see there's no action available.
            if (!string.IsNullOrWhiteSpace(status) && status != "pending")
            {
                var pill = new Button
                {
                    Style = (Style)FindResource("SecondaryButton"),
                    Content = status == "delivered" ? L("Referrals_Reward_Action_Delivered", "Delivered") :
                              status == "approved"  ? L("Referrals_Reward_Action_Approved", "Approved")  :
                              status == "rejected"  ? L("Referrals_Reward_Action_SeeNote", "See note")  :
                                                      L("Referrals_Reward_Action_Submitted", "Submitted"),
                    MinWidth = 100,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsEnabled = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return pill;
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CLAIM PANEL
        // ══════════════════════════════════════════════════════════════════

        private void OpenClaimPanelFor(ReferralReward reward)
        {
            _activeClaimRewardId = reward.Id;
            ClaimRewardSummary.Text = LF(
                "Referrals_Claim_Summary_Format",
                "You're claiming {0}. We review claims manually (usually within 48h) and email you when it ships.",
                DescribeRewardTitle(reward));

            // Reset inputs to a clean state on every open.
            HoyolabUidBox.Text = string.Empty;
            EmailBox.Text = string.Empty;
            ClaimErrorText.Visibility = Visibility.Collapsed;
            ClaimErrorText.Text = string.Empty;
            RadioHoyolab.IsChecked = true;
            UpdateClaimInputVisibility();
            ValidateClaimInputs(); // refresh button state

            ClaimPanel.Visibility = Visibility.Visible;

            // Scroll the panel into view so the user sees it even on small
            // windows where the rewards list dominates.
            ClaimPanel.BringIntoView();
        }

        private void ClaimCancel_Click(object sender, RoutedEventArgs e)
        {
            _activeClaimRewardId = null;
            ClaimPanel.Visibility = Visibility.Collapsed;
        }

        private void DeliveryMethod_Changed(object sender, RoutedEventArgs e)
        {
            // Ignore checked events that fire during initial construction,
            // before the claim panel has been opened and the inputs wired up.
            if (!IsLoaded) return;
            UpdateClaimInputVisibility();
            ValidateClaimInputs();
        }

        private void HoyolabRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            ValidateClaimInputs();
        }

        private void ClaimField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            ValidateClaimInputs();
        }

        /// <summary>
        /// Swap the input block shown below the delivery-method radios so
        /// the user only sees fields relevant to their pick.
        /// </summary>
        private void UpdateClaimInputVisibility()
        {
            bool isHoyolab = RadioHoyolab.IsChecked == true;
            HoyolabInputPanel.Visibility = isHoyolab ? Visibility.Visible : Visibility.Collapsed;
            EmailInputPanel.Visibility = isHoyolab ? Visibility.Collapsed : Visibility.Visible;

            if (!isHoyolab)
            {
                EmailLabel.Text = RadioPaypal.IsChecked == true
                    ? L("Referrals_Claim_Email_PayPal", "PayPal email")
                    : L("Referrals_Claim_Email_Amazon", "Amazon account email");
            }
        }

        /// <summary>
        /// Enable the Submit button only when inputs look plausibly valid.
        /// Does NOT show an error on failed validation — that's reserved for
        /// the submit path, where a mistake is relevant to the user.
        /// </summary>
        private void ValidateClaimInputs()
        {
            if (_submittingClaim)
            {
                BtnSubmitClaim.IsEnabled = false;
                return;
            }

            bool ok;
            if (RadioHoyolab.IsChecked == true)
            {
                string uid = (HoyolabUidBox.Text ?? "").Trim();
                ok = HoyolabUidRegex().IsMatch(uid);
            }
            else
            {
                string email = (EmailBox.Text ?? "").Trim();
                ok = EmailRegex().IsMatch(email) && email.Length <= 200;
            }
            BtnSubmitClaim.IsEnabled = ok;
        }

        private async void SubmitClaim_Click(object sender, RoutedEventArgs e)
        {
            if (_submittingClaim) return;
            if (string.IsNullOrEmpty(_activeClaimRewardId))
            {
                ShowClaimError(L("Referrals_Claim_Error_NoReward", "Internal error: no reward selected."));
                return;
            }

            string deliveryMethod;
            string contactInfo;
            if (!BuildClaimPayload(out deliveryMethod, out contactInfo, out string validationError))
            {
                ShowClaimError(validationError);
                return;
            }

            string jwt = App.LicenseService?.CurrentActivation?.DeviceSessionJwt;
            if (string.IsNullOrEmpty(jwt))
            {
                ShowClaimError(L("Referrals_Claim_Error_Expired", "Your session has expired. Close this window, sign in, and retry."));
                return;
            }

            SetClaimSubmittingState(true);
            ShowClaimError(null);

            try
            {
                using (var cts = new CancellationTokenSource(RequestTimeout))
                {
                    var updatedReward = await _api.ClaimReferralRewardAsync(
                            jwt,
                            _activeClaimRewardId,
                            deliveryMethod,
                            contactInfo,
                            cts.Token)
                        .ConfigureAwait(true);

                    // Merge the updated reward back into the local state so
                    // the rewards list re-renders with the new status.
                    if (updatedReward != null && _current?.Rewards != null)
                    {
                        var idx = _current.Rewards.FindIndex(r => r.Id == updatedReward.Id);
                        if (idx >= 0)
                            _current.Rewards[idx] = updatedReward;
                        else
                            _current.Rewards.Add(updatedReward);
                        PopulateRewards(_current.Rewards);
                    }
                    else
                    {
                        // Server didn't echo the row — refresh wholesale.
                        _ = LoadAsync();
                    }
                }

                // Collapse the panel on success.
                _activeClaimRewardId = null;
                ClaimPanel.Visibility = Visibility.Collapsed;
            }
            catch (UnauthorizedException)
            {
                ShowClaimError(L("Referrals_Claim_Error_Expired", "Your session has expired. Close this window, sign in, and retry."));
            }
            catch (ForbiddenException ex)
            {
                ShowClaimError(ex.Message ?? L("Referrals_Claim_Error_Forbidden", "You don't have permission to claim this reward."));
            }
            catch (ApiValidationException ex)
            {
                int code = (int)ex.StatusCode;
                if (code == 409)
                    ShowClaimError(L("Referrals_Claim_Error_AlreadyClaimed", "This reward has already been claimed."));
                else
                    ShowClaimError(ex.Error?.Describe() ?? L("Referrals_Claim_Error_Rejected", "The server rejected the claim."));
            }
            catch (ApiUnavailableException)
            {
                ShowClaimError(L("Referrals_Claim_Error_Unreachable", "We couldn't reach Kaption's servers. Check your connection and try again."));
            }
            catch (OperationCanceledException)
            {
                ShowClaimError(L("Referrals_Claim_Error_Timeout", "The request took too long. Please try again."));
            }
            catch (Exception ex)
            {
                // Never log the contact_info — keep the message generic here.
                Logger.Log.Error($"Claim submit failed for reward {_activeClaimRewardId}: {ex.GetType().Name}");
                ShowClaimError(L("Referrals_Claim_Error_Generic", "Something went wrong. Please try again."));
            }
            finally
            {
                SetClaimSubmittingState(false);
            }
        }

        /// <summary>
        /// Build <c>delivery_method</c> + <c>contact_info</c> from the form,
        /// applying the same format rules the server enforces. Out-parameter
        /// pattern keeps the happy path clear.
        /// </summary>
        private bool BuildClaimPayload(out string deliveryMethod, out string contactInfo, out string error)
        {
            deliveryMethod = null;
            contactInfo = null;
            error = null;

            if (RadioHoyolab.IsChecked == true)
            {
                string uid = (HoyolabUidBox.Text ?? "").Trim();
                if (!HoyolabUidRegex().IsMatch(uid))
                {
                    error = L("Referrals_Claim_Error_InvalidUid", "HoYoLAB UID must be 9 to 12 digits.");
                    return false;
                }
                string regionTag = "NA";
                if (HoyolabRegionCombo.SelectedItem is ComboBoxItem item && item.Tag is string t)
                    regionTag = t;
                deliveryMethod = "hoyolab_uid";
                contactInfo = $"{uid} / {regionTag}";
                return true;
            }

            if (RadioPaypal.IsChecked == true || RadioAmazon.IsChecked == true)
            {
                string email = (EmailBox.Text ?? "").Trim();
                if (!EmailRegex().IsMatch(email) || email.Length > 200)
                {
                    error = L("Referrals_Claim_Error_InvalidEmail", "Please enter a valid email.");
                    return false;
                }
                deliveryMethod = RadioPaypal.IsChecked == true ? "paypal" : "amazon_gc";
                contactInfo = email;
                return true;
            }

            error = L("Referrals_Claim_Error_NoMethod", "Please pick a delivery method.");
            return false;
        }

        private void SetClaimSubmittingState(bool submitting)
        {
            _submittingClaim = submitting;
            BtnSubmitClaim.Content = submitting
                ? L("Referrals_Claim_Submitting", "Submitting…")
                : L("Referrals_Claim_Submit", "Submit claim");
            Mouse.OverrideCursor = submitting ? Cursors.Wait : null;
            // Keep inputs enabled during submit so the user sees what they
            // typed, but disable the submit button itself.
            BtnSubmitClaim.IsEnabled = !submitting;
        }

        private void ShowClaimError(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ClaimErrorText.Visibility = Visibility.Collapsed;
                ClaimErrorText.Text = string.Empty;
            }
            else
            {
                ClaimErrorText.Text = message;
                ClaimErrorText.Visibility = Visibility.Visible;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  ATTRIBUTE A FRIEND'S CODE
        // ══════════════════════════════════════════════════════════════════

        private void AttributeCode_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            string code = (AttributeCodeBox.Text ?? "").Trim().ToUpperInvariant();
            BtnAttributeSubmit.IsEnabled = !_submittingAttribution && ReferralCodeRegex().IsMatch(code);
            if (AttributeStatusText.Visibility == Visibility.Visible)
                AttributeStatusText.Visibility = Visibility.Collapsed;
        }

        private async void AttributeSubmit_Click(object sender, RoutedEventArgs e)
        {
            if (_submittingAttribution) return;

            string code = (AttributeCodeBox.Text ?? "").Trim().ToUpperInvariant();
            if (!ReferralCodeRegex().IsMatch(code))
            {
                ShowAttributeStatus(L("Referrals_Attribute_Invalid", "That doesn't look like a valid code. Ask your friend to send it again."), isError: true);
                return;
            }

            string jwt = App.LicenseService?.CurrentActivation?.DeviceSessionJwt;
            if (string.IsNullOrEmpty(jwt))
            {
                ShowAttributeStatus(L("Referrals_Attribute_NotSignedIn", "You need to be signed in to link a referrer."), isError: true);
                return;
            }

            _submittingAttribution = true;
            BtnAttributeSubmit.IsEnabled = false;
            BtnAttributeSubmit.Content = L("Referrals_Attribute_Submitting", "Submitting…");

            try
            {
                using (var cts = new CancellationTokenSource(RequestTimeout))
                {
                    await _api.AttributeReferralAsync(jwt, code, cts.Token).ConfigureAwait(true);
                }

                ShowAttributeStatus(L("Referrals_Attribute_Thanks", "Thanks! Your friend's code is linked. You're both set up."), isError: false);
                try { Core.Config.Config.Set("ReferralCodeAttributedOrSkipped", true); }
                catch (Exception ex) { Logger.Log.Warn($"Could not persist ReferralCodeAttributedOrSkipped: {ex.Message}"); }
                AttributeCodeBox.IsEnabled = false;
            }
            catch (UnauthorizedException)
            {
                ShowAttributeStatus(L("Referrals_Attribute_Expired", "Your session expired. Close this window, sign in, and retry."), isError: true);
            }
            catch (ForbiddenException ex)
            {
                // 403 = self-referral or other access denial.
                ShowAttributeStatus(ex.Message ?? L("Referrals_Attribute_SelfReferral", "That code can't be linked to your account."), isError: true);
            }
            catch (ApiValidationException ex)
            {
                int code2 = (int)ex.StatusCode;
                string serverMsg = ex.Error?.Describe();
                string msg = code2 switch
                {
                    409 => L("Referrals_Attribute_Conflict", "Looks like you're already linked to a referrer, or this code has already been used."),
                    400 => serverMsg ?? L("Referrals_Attribute_PastWindow", "That code isn't valid, or your account is past the 7-day window."),
                    _   => serverMsg ?? L("Referrals_Attribute_Rejected", "The server rejected the request."),
                };
                ShowAttributeStatus(msg, isError: true);
            }
            catch (ApiUnavailableException)
            {
                ShowAttributeStatus(L("Referrals_Attribute_Unreachable", "We couldn't reach Kaption's servers. Check your connection and try again."), isError: true);
            }
            catch (OperationCanceledException)
            {
                ShowAttributeStatus(L("Referrals_Attribute_Timeout", "The request took too long. Please try again."), isError: true);
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Attribute referral failed: {ex.GetType().Name}");
                ShowAttributeStatus(L("Referrals_Attribute_Generic", "Something went wrong. Please try again."), isError: true);
            }
            finally
            {
                _submittingAttribution = false;
                BtnAttributeSubmit.Content = L("Referrals_Attribute_Submit", "Submit");
                // Re-enable only if the code still looks valid AND we haven't
                // locked the field after a success.
                if (AttributeCodeBox.IsEnabled)
                {
                    string c = (AttributeCodeBox.Text ?? "").Trim().ToUpperInvariant();
                    BtnAttributeSubmit.IsEnabled = ReferralCodeRegex().IsMatch(c);
                }
            }
        }

        private void ShowAttributeStatus(string message, bool isError)
        {
            AttributeStatusText.Text = message;
            AttributeStatusText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C))
                : (Brush)FindResource("SuccessBrush");
            AttributeStatusText.Visibility = Visibility.Visible;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CLIPBOARD + NAVIGATION
        // ══════════════════════════════════════════════════════════════════

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            string code = _current?.Code;
            if (string.IsNullOrWhiteSpace(code)) return;
            TrySetClipboard(code, L("Referrals_Toast_CodeCopied", "Code copied"));
        }

        private void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            string url = _current?.ShareUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            TrySetClipboard(url, L("Referrals_Toast_LinkCopied", "Link copied"));
        }

        /// <summary>
        /// Writes <paramref name="text"/> to the clipboard and flashes a
        /// transient confirmation on the button's content. Clipboard.SetText
        /// can throw <see cref="ExternalException"/> / other COM errors when
        /// another app has a lock on the clipboard — we swallow and just log
        /// in that case; a failed copy is annoying but not fatal.
        /// </summary>
        private void TrySetClipboard(string text, string confirmLabel)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                // Briefly swap the page title to confirm. Keeps the flow
                // simple — no toast framework, no snackbar.
                var originalTitle = this.Title;
                this.Title = confirmLabel;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.2),
                };
                timer.Tick += (_, __) => { timer.Stop(); this.Title = originalTitle; };
                timer.Start();
            }
            catch (Exception ex)
            {
                // Don't log the text itself — it's a share URL in one case and
                // a referral code in the other; both benign, but keep the bar
                // high.
                Logger.Log.Warn($"Clipboard copy failed: {ex.GetType().Name}");
            }
        }

        private void OpenReferrals_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser(ReferralsWebUrl);
        }

        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not open URL '{url}': {ex.Message}");
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true,
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not open referrals URL '{e.Uri}': {ex.Message}");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // ══════════════════════════════════════════════════════════════════
        //  VIEW STATE
        // ══════════════════════════════════════════════════════════════════

        private enum ViewState { Loading, Content, Error }

        private void SetViewState(ViewState state)
        {
            LoadingPanel.Visibility = state == ViewState.Loading ? Visibility.Visible : Visibility.Collapsed;
            ContentScroll.Visibility = state == ViewState.Content ? Visibility.Visible : Visibility.Collapsed;
            ErrorPanel.Visibility = state == ViewState.Error   ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            ErrorMessageText.Text = message ?? L("Referrals_LoadError_Unknown", "Unknown error.");
            SetViewState(ViewState.Error);
        }
    }
}

