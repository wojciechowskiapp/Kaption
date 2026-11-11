using System;
using System.Windows;
using System.Windows.Media;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Notices;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The "new story is in Polish" notice in Settings: its one line on Start and its switch on Gra i napisy. The rule
    /// and the memory are Services/Notices/NewStoryNotices.cs; MainWindow.NewStory.cs shows the tray balloon.
    /// </summary>
    public partial class SettingsWindow
    {
        private NewStoryNotice _newStory;
        private bool _syncingNewStoryToggle;

        private void InitializeNewStory()
        {
            SyncNewStoryToggle();
            NewStoryNotices.Default.Changed += OnNewStoryChanged;
            Closed += (_, __) => NewStoryNotices.Default.Changed -= OnNewStoryChanged;
            RefreshNewStoryLine();
        }

        // May fire on the pack sync's thread; BeginInvoke does not throw once the dispatcher is shutting down.
        private void OnNewStoryChanged()
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(RefreshNewStoryLine));
        }

        private void SyncNewStoryToggle()
        {
            if (NewStoryToggle == null) return;
            _syncingNewStoryToggle = true;
            try { NewStoryToggle.IsChecked = NewStoryNotices.Default.Enabled; }
            finally { _syncingNewStoryToggle = false; }
        }

        private void NewStoryToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _syncingNewStoryToggle) return;
            bool on = NewStoryToggle.IsChecked == true;
            Config.Set(NewStoryNotices.EnabledKey, on);
            Logger.Log.Info($"New story notices turned {(on ? "on" : "off")}.");
            NewStoryNotices.Default.OnEnabledChanged();
        }

        /// <summary>Shows the pending line, or hides the card; the What's new card steps aside when it says the same.</summary>
        private void RefreshNewStoryLine()
        {
            if (NewStoryCard == null) return;
            _newStory = NewStoryNotices.Default.Current(DateTimeOffset.UtcNow);
            ApplyNewStoryLineTo(FindName, _newStory, L, key => TryFindResource(key) as Brush);
            ApplyNewStoryOverWhatsNew();
        }

        /// <summary>Fills and shows the line for <paramref name="notice"/>, or hides the card for null (the render tests call this).</summary>
        internal static void ApplyNewStoryLineTo(Func<string, object> find, NewStoryNotice notice, Func<string, string, string> L, Func<string, Brush> brush)
        {
            if (!(find("NewStoryCard") is UIElement card)) return;
            if (notice == null)
            {
                card.Visibility = Visibility.Collapsed;
                return;
            }
            if (find("NewStoryLine") is System.Windows.Controls.TextBlock line)
                line.Text = NewStoryText.Line(L("NewStory_Line_Format", "{0} {1}: the new story is in Polish now"),
                    GameRegionProfile.DisplayNameOf(notice.Game), notice.Version);
            if (find("NewStoryAccent") is System.Windows.Controls.Border edge && brush(AccentKeyFor(notice.Game)) is Brush accent)
                edge.Background = accent;
            card.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// One message per event. While the line is up, the What's new card for the same version's article hides; once
        /// the player closed or opened the notice (here or from the tray, before or after the feed loaded), that card
        /// is marked seen, or it would pop up in the line's place.
        /// </summary>
        private void ApplyNewStoryOverWhatsNew()
        {
            if (WhatsNewCard == null || _whatsNew == null) return;
            switch (WhatsNewUnderNewStory(
                        acknowledged: NewStoryNotices.Default.Acknowledges(_whatsNew.Game, _whatsNew.GameVersion, _whatsNew.Kind),
                        covered: NewStoryText.CoversReleaseNote(_newStory, _whatsNew.Game, _whatsNew.GameVersion, _whatsNew.Kind),
                        seen: string.Equals(Config.Get<string>(WhatsNewSeenKey, null), _whatsNew.ReleaseId, StringComparison.Ordinal)))
            {
                case WhatsNewFate.MarkSeen:
                    Config.Set(WhatsNewSeenKey, _whatsNew.ReleaseId);
                    WhatsNewCard.Visibility = Visibility.Collapsed;
                    break;
                case WhatsNewFate.Hide:
                    WhatsNewCard.Visibility = Visibility.Collapsed;
                    break;
                case WhatsNewFate.Show:
                    WhatsNewCard.Visibility = Visibility.Visible;
                    break;
            }
        }

        internal enum WhatsNewFate { Leave, Show, Hide, MarkSeen }

        /// <summary>
        /// What happens to a What's new card that is on (fresh, loaded): acknowledged news closes it for good, a line
        /// that is up hides it, and a line that went away without being closed (the switch turned off) gives it back.
        /// </summary>
        internal static WhatsNewFate WhatsNewUnderNewStory(bool acknowledged, bool covered, bool seen) =>
            seen ? WhatsNewFate.Leave
            : acknowledged ? WhatsNewFate.MarkSeen
            : covered ? WhatsNewFate.Hide
            : WhatsNewFate.Show;

        // Opening the page closes the line only when the page really opened; a failed launch leaves it to try again.
        private void NewStoryOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_newStory != null) MainWindow.OpenNewStoryPage(_newStory);
        }

        private void NewStoryDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (_newStory != null) NewStoryNotices.Default.Dismiss(_newStory);
        }
    }
}
