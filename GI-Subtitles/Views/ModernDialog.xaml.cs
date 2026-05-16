// ─────────────────────────────────────────────────────────────────────────────
//  ModernDialog.xaml.cs
//  ---------------------------------------------------------------------------
//  Branded replacement for System.Windows.MessageBox.Show. The goal is one
//  reusable dialog whose look matches the Settings window (same ModernStyles
//  resources, same rounded-card aesthetic, same palette) instead of the
//  native Win32 message box that stands out like a sore thumb in a mostly-
//  custom UI.
//
//  Call sites use the static helpers — see bottom of file. They map to:
//
//    ModernDialog.Info(owner, title, body)           → OK-only, blue accent
//    ModernDialog.Success(...)                       → OK-only, emerald
//    ModernDialog.Warn(...)                          → OK-only, amber
//    ModernDialog.Error(owner, title, body, details) → OK-only, red, with
//                                                      a collapsible details panel
//    ModernDialog.Confirm(owner, title, body,
//                         primary, secondary)        → returns true on primary,
//                                                      false otherwise
//
//  Placement & visibility:
//    * Centred on the owning window when one is provided (CenterOwner).
//    * Centred on the primary screen — nudged up toward the top third —
//      when no owner exists (pre-MainWindow: App.OnStartup licensing /
//      crash-opt-in / fatal-error dialogs). This is the "pops up at top so
//      the user actually sees it" behaviour requested by the user — it's
//      especially important for the opt-in dialog which fires before the
//      main window has painted.
//    * Topmost=true so a background/minimised app still surfaces the dialog.
//
//  Threading:
//    * All static helpers marshal to the WPF dispatcher automatically. Safe
//      to call from any thread, including non-UI worker threads where a raw
//      MessageBox.Show would throw.
//
//  Accessibility:
//    * Enter activates the primary button (IsDefault=True).
//    * Esc closes the dialog with the "dismiss" result (false for Confirm,
//      void for the informational variants).
//    * Tab order respects Secondary → Primary so screen-readers / keyboard
//      users can navigate without a mouse.
//    * The whole card is draggable (MouseLeftButtonDown on the outer border)
//      because we run frameless; users who prefer to move dialogs away from
//      a video they're watching can.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Visual tone for a dialog. Drives the accent-strip color, the icon,
    /// and the icon-bubble tint. Nothing else — copy is set by the caller.
    /// </summary>
    public enum DialogSeverity
    {
        /// <summary>Neutral announcement. Blue accent, info-i glyph.</summary>
        Info,
        /// <summary>Something positive happened. Emerald accent, check glyph.</summary>
        Success,
        /// <summary>Attention needed but not broken. Amber accent, triangle glyph.</summary>
        Warn,
        /// <summary>Something failed. Red accent, ring-with-cross glyph.</summary>
        Error,
        /// <summary>Asking the user to decide. Blue accent, speech-bubble glyph.</summary>
        Question,
    }

    /// <summary>
    /// Reusable branded dialog. Use the static helpers unless you need
    /// per-call customisation beyond what they expose.
    /// </summary>
    public partial class ModernDialog : Window
    {
        /// <summary>
        /// True when the user clicked the primary action. False for Close /
        /// Secondary / Esc. The static helpers read this before returning.
        /// </summary>
        public bool PrimaryClicked { get; private set; }

        private ModernDialog()
        {
            InitializeComponent();
        }

        // ────────────────────────────────────────────────────────────────────
        //  Static API
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resource lookup helper — when a caller passes null for a button
        /// label we fall back to the localized Common_* strings so a Polish
        /// session shows "OK"/"Tak"/"Nie" instead of the baked English.
        /// </summary>
        private static string L(string key, string fallback)
            => Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Shorthand for a neutral OK-only dialog.</summary>
        public static void Info(Window owner, string title, string body, string details = null)
            => ShowOkDialog(owner, title, body, details, DialogSeverity.Info, L("Common_OK", "OK"));

        /// <summary>Shorthand for a success OK-only dialog.</summary>
        public static void Success(Window owner, string title, string body, string details = null)
            => ShowOkDialog(owner, title, body, details, DialogSeverity.Success, L("Common_OK", "OK"));

        /// <summary>Shorthand for a warning OK-only dialog.</summary>
        public static void Warn(Window owner, string title, string body, string details = null)
            => ShowOkDialog(owner, title, body, details, DialogSeverity.Warn, L("Common_OK", "OK"));

        /// <summary>
        /// OK-only error dialog. When <paramref name="technicalDetails"/> is
        /// non-empty, a "Show technical details" expander appears with the
        /// details in a monospace read-only box — hidden by default so the
        /// body stays clean for non-technical users.
        /// </summary>
        public static void Error(Window owner, string title, string body, string technicalDetails = null)
            => ShowOkDialog(owner, title, body, technicalDetails, DialogSeverity.Error, L("Common_OK", "OK"), technicalIsExpander: true);

        /// <summary>
        /// Yes/No style confirm. Returns true when the user clicks the primary
        /// action, false for anything else (Secondary / Close / Esc).
        /// Primary defaults to "Yes", Secondary to "No" — override for verbs
        /// ("Install", "Update later", "Delete", "Cancel", etc).
        ///
        /// <para><paramref name="dangerousPrimary"/>: when true, the primary
        /// action is the one that *requires deliberate opt-in* (e.g. "Continue
        /// anyway", "Delete"). Visually the secondary button then gets the
        /// bold default styling and Enter activates it, while the primary
        /// button shrinks to subtle secondary styling. The return semantics
        /// don't change — primary-click still returns true, Esc/Close still
        /// returns false — so Esc is always the safe fall-through.</para>
        /// </summary>
        public static bool Confirm(
            Window owner,
            string title,
            string body,
            string primaryText = null,
            string secondaryText = null,
            DialogSeverity severity = DialogSeverity.Question,
            string details = null,
            bool dangerousPrimary = false)
        {
            return RunOnUi(owner, () =>
            {
                var dlg = CreateDialog(
                    owner, title, body, details, severity,
                    primaryText ?? L("Common_Yes", "Yes"),
                    secondaryText ?? L("Common_No", "No"),
                    technicalIsExpander: false);

                if (dangerousPrimary)
                {
                    // Swap visual weight so the caller's "safe" action (the
                    // secondary button) becomes the bold, Enter-activated
                    // one. Click-wiring stays the same, so Esc still returns
                    // false (caller's "safe" path).
                    dlg.PrimaryButton.Style = (Style)dlg.FindResource("SecondaryButton");
                    dlg.PrimaryButton.IsDefault = false;
                    dlg.SecondaryButton.Style = (Style)dlg.FindResource("ModernButton");
                    dlg.SecondaryButton.IsDefault = true;
                }

                dlg.ShowDialog();
                return dlg.PrimaryClicked;
            });
        }

        // ────────────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────────────

        private static void ShowOkDialog(
            Window owner, string title, string body, string details,
            DialogSeverity severity, string primaryText, bool technicalIsExpander = false)
        {
            RunOnUi(owner, () =>
            {
                var dlg = CreateDialog(owner, title, body, details, severity,
                                       primaryText, secondaryText: null,
                                       technicalIsExpander: technicalIsExpander);
                dlg.ShowDialog();
                return true; // return value ignored for void variants
            });
        }

        /// <summary>
        /// Build a configured ModernDialog. Never blocks — caller is expected
        /// to call ShowDialog separately.
        /// </summary>
        private static ModernDialog CreateDialog(
            Window owner,
            string title,
            string body,
            string details,
            DialogSeverity severity,
            string primaryText,
            string secondaryText,
            bool technicalIsExpander)
        {
            var dlg = new ModernDialog();

            // Ownership + placement. CenterOwner keeps the dialog over the
            // triggering window; CenterScreen (with a small upward nudge) is
            // the fallback for pre-MainWindow dialogs.
            if (owner != null && owner.IsLoaded)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                // Nudge up so the dialog lives in the upper third of the
                // screen — user explicitly asked for "pops up at top so user
                // sees it", and a centered dialog often sits *behind* the
                // viewer's gaze on a game screen.
                dlg.SourceInitialized += (s, _) => NudgeToUpperThird(dlg);
            }

            dlg.Title = title ?? "Kaption";
            dlg.TitleText.Text = title ?? string.Empty;
            dlg.BodyText.Text = body ?? string.Empty;

            // Details: either as a subtle sub-paragraph (`Info`/`Confirm`
            // flow) or behind an expander (`Error` flow with raw exception).
            if (!string.IsNullOrWhiteSpace(details))
            {
                if (technicalIsExpander)
                {
                    dlg.DetailsExpander.Visibility = Visibility.Visible;
                    dlg.DetailsBox.Text = details;
                }
                else
                {
                    dlg.DetailsText.Visibility = Visibility.Visible;
                    dlg.DetailsText.Text = details;
                }
            }

            dlg.PrimaryButton.Content = primaryText ?? L("Common_OK", "OK");
            if (!string.IsNullOrEmpty(secondaryText))
            {
                dlg.SecondaryButton.Visibility = Visibility.Visible;
                dlg.SecondaryButton.Content = secondaryText;
            }

            ApplySeverity(dlg, severity);
            return dlg;
        }

        /// <summary>
        /// Marshal to the WPF dispatcher if the caller is on a background
        /// thread. Safe to call with owner=null (uses Application.Current).
        /// </summary>
        private static T RunOnUi<T>(Window owner, Func<T> body)
        {
            var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return body();
            return dispatcher.Invoke(body);
        }

        /// <summary>
        /// After the window source has initialised (so ActualHeight is known)
        /// push it up to roughly the top third of the primary screen. Kept
        /// bounded so tall dialogs on small screens don't end up off-top.
        /// </summary>
        private static void NudgeToUpperThird(Window w)
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double desiredTop = wa.Top + Math.Max(24, wa.Height * 0.12);
                if (w.Height > 0 && desiredTop + w.Height < wa.Bottom - 24)
                    w.Top = desiredTop;
            }
            catch { /* non-fatal — fall back to CenterScreen */ }
        }

        /// <summary>
        /// Translate a <see cref="DialogSeverity"/> to the accent-strip color,
        /// bubble fill, and glyph path. Keeps all colour/glyph decisions in
        /// one place — call sites never pick colours themselves.
        /// </summary>
        private static void ApplySeverity(ModernDialog dlg, DialogSeverity severity)
        {
            Color accent;
            Color bubbleBg;
            string pathData;

            switch (severity)
            {
                case DialogSeverity.Success:
                    accent   = ColorFromHex("#059669");                  // emerald
                    bubbleBg = ColorFromHex("#D1FAE5");
                    // Checkmark inside a circle.
                    pathData = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm-1.2 14.2L6.6 12l1.4-1.4 2.8 2.8 5.2-5.2 1.4 1.4-6.6 6.6z";
                    break;
                case DialogSeverity.Warn:
                    accent   = ColorFromHex("#D97706");                  // amber
                    bubbleBg = ColorFromHex("#FEF3C7");
                    // Triangle with exclamation point.
                    pathData = "M12 2L1 21h22L12 2zm1 14h-2v-2h2v2zm0-4h-2V8h2v4z";
                    break;
                case DialogSeverity.Error:
                    accent   = ColorFromHex("#DC2626");                  // red
                    bubbleBg = ColorFromHex("#FEE2E2");
                    // Circle with a cross.
                    pathData = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm4.24 13.41l-1.41 1.41L12 13.41l-2.83 2.83-1.41-1.41L10.59 12 7.76 9.17l1.41-1.41L12 10.59l2.83-2.83 1.41 1.41L13.41 12l2.83 2.83z";
                    break;
                case DialogSeverity.Question:
                    accent   = ColorFromHex("#2563EB");                  // blue
                    bubbleBg = ColorFromHex("#DBEAFE");
                    // Speech bubble with '?' inside — for consent / confirm prompts.
                    pathData = "M20 2H4a2 2 0 0 0-2 2v18l4-4h14a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2zm-7 14h-2v-2h2v2zm2.07-7.75l-.9.92C13.45 9.9 13 10.5 13 12h-2v-.5c0-1.1.45-2.1 1.17-2.83l1.24-1.26c.37-.36.59-.86.59-1.41 0-1.1-.9-2-2-2s-2 .9-2 2H8c0-2.21 1.79-4 4-4s4 1.79 4 4c0 .88-.36 1.68-.93 2.25z";
                    break;
                default: // Info
                    accent   = ColorFromHex("#2563EB");                  // blue
                    bubbleBg = ColorFromHex("#DBEAFE");
                    // Lowercase 'i' inside a circle.
                    pathData = "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z";
                    break;
            }

            dlg.AccentStrip.Background = new SolidColorBrush(accent);
            dlg.IconBubble.Background  = new SolidColorBrush(bubbleBg);
            dlg.IconPath.Fill          = new SolidColorBrush(accent);
            dlg.IconPath.Data          = Geometry.Parse(pathData);
        }

        private static Color ColorFromHex(string hex)
            => (Color)ColorConverter.ConvertFromString(hex);

        // ────────────────────────────────────────────────────────────────────
        //  Event handlers
        // ────────────────────────────────────────────────────────────────────

        private void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            PrimaryClicked = true;
            Close();
        }

        private void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            PrimaryClicked = false;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            PrimaryClicked = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Esc dismisses with the "not primary" result. Enter is already
            // handled by IsDefault=True on PrimaryButton.
            if (e.Key == Key.Escape)
            {
                PrimaryClicked = false;
                Close();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Drag the window when the user presses down anywhere on the card
        /// background (we're frameless, so WPF doesn't provide drag-to-move
        /// automatically). We only engage on LeftButton to avoid fighting
        /// the close button's own click handling.
        /// </summary>
        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { /* fires if mouse already up */ }
            }
        }
    }
}
