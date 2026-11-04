using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The farm card's place in Settings › Characters (Genshin only): today's books, "Show the card", and the UID
    /// (saved only on the player's own click, with the consent line beside it), the server and the startup switch.
    /// </summary>
    public partial class FarmTodayBlock : UserControl
    {
        private bool _filling;
        private int _refresh;

        public FarmTodayBlock()
        {
            InitializeComponent();
        }

        /// <summary>Shown only for Genshin; reloads today's books and the saved settings.</summary>
        public void Refresh(string game)
        {
            bool genshin = FarmCardSettings.IsGame(game);
            Visibility = genshin ? Visibility.Visible : Visibility.Collapsed;
            if (!genshin) return;
            FillSettings();
            _ = LoadBooksAsync(++_refresh);
        }

        /// <summary>Opens the settings with the UID field focused ("Add UID" on the card).</summary>
        public void OpenSettings(bool focusUid)
        {
            SettingsToggle.IsChecked = true;
            ApplyExpanded();
            if (focusUid) Dispatcher.BeginInvoke(new Action(() => { UidBox.Focus(); UidBox.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
            BringIntoView();
        }

        private void FillSettings()
        {
            _filling = true;
            try
            {
                string uid = FarmCardSettings.Uid;
                string suggested = FarmCardSettings.SuggestedUid;
                UidBox.Text = uid ?? suggested ?? "";
                ForgetButton.IsEnabled = uid != null;
                if (uid == null && suggested != null)
                    Note(string.Format(CultureInfo.CurrentCulture, L("FarmBlock_Suggested_Format", "Kaption read {0} from the game screen. Save it to use it here."), suggested), muted: true);
                else Note(null);
                string region = ServerDay.Id(FarmCardSettings.Region);
                RegionBox.SelectedItem = RegionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == region) ?? RegionBox.Items[0];
                EnabledBox.IsChecked = FarmCardSettings.Enabled;
            }
            finally
            {
                _filling = false;
            }
        }

        private async Task LoadBooksAsync(int ticket)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var day = ServerDay.At(now, FarmCardSettings.Region);
                string lang = UiLanguage.Current;
                string dayName = CultureInfo.GetCultureInfo(lang).DateTimeFormat.GetDayName(day.Weekday);
                DayRun.Text = " · " + dayName;
                var schedule = await FarmTodayData.ScheduleAsync(day);
                if (ticket != _refresh) return;
                if (schedule == null)
                {
                    Tiles.ItemsSource = null;
                    return;
                }
                var catalog = await CharacterCatalogService.GetAsync(FarmCardSettings.GameId);
                // The books only: the player's rows belong to the card.
                var model = FarmCardBuilder.Build(schedule, day, now, hasUid: false, roster: null, catalog, lang);
                var cache = CharacterImageCache.ForGame(FarmCardSettings.GameId);
                var missing = model.Books.Select(b => b.IconUrl).Where(u => u != null && cache.CachedPathOrNull(u) == null).Select(u => cache.GetAsync(u)).ToList();
                if (missing.Count > 0) await Task.WhenAny(Task.WhenAll(missing), Task.Delay(TimeSpan.FromSeconds(4)));
                if (ticket != _refresh) return;
                foreach (var b in model.Books) b.IconPath = cache.CachedPathOrNull(b.IconUrl);
                var vm = new FarmCardViewModel(model, lang, L);
                Tiles.ItemsSource = vm.Books;
                Lead.Text = model.IsSunday ? vm.TwinText : L("FarmBlock_Lead", "A card over the game once a day, when you start Genshin Impact.");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm block: today's books not loaded: {ex.Message}");
            }
        }

        private void ShowButton_Click(object sender, RoutedEventArgs e) =>
            (Window.GetWindow(this) as SettingsWindow)?.OnShowFarmCard?.Invoke();

        private void SettingsToggle_Click(object sender, RoutedEventArgs e) => ApplyExpanded();

        private void ApplyExpanded()
        {
            bool open = SettingsToggle.IsChecked == true;
            SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            Chevron.RenderTransform = new RotateTransform(open ? 180 : 0);
        }

        private void UidBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Save();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            if (!FarmCardSettings.SaveUid(UidBox.Text))
            {
                Note(L("FarmBlock_Invalid", "A UID has 9 or 10 digits."), muted: false);
                return;
            }
            Logger.Log.Info("Farm card: UID saved.");
            ForgetButton.IsEnabled = true;
            Note(L("FarmBlock_Saved", "Saved. The card now shows your characters."), muted: true);
        }

        private void ForgetButton_Click(object sender, RoutedEventArgs e)
        {
            FarmTodayData.ForgetPlayer();
            UidBox.Text = "";
            ForgetButton.IsEnabled = false;
            Note(L("FarmBlock_Forgotten", "Removed. Kaption no longer reads your showcase."), muted: true);
        }

        private void RegionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || !IsLoaded || !(RegionBox.SelectedItem is ComboBoxItem item)) return;
            FarmCardSettings.Region = ServerDay.Parse(item.Tag as string);
            _ = LoadBooksAsync(++_refresh);
        }

        private void EnabledBox_Click(object sender, RoutedEventArgs e)
        {
            if (_filling || !IsLoaded) return;
            FarmCardSettings.Enabled = EnabledBox.IsChecked == true;
            Logger.Log.Info($"Farm card: {(FarmCardSettings.Enabled ? "on" : "off")} at game start.");
        }

        private void Note(string text, bool muted = true)
        {
            UidNote.Text = text ?? "";
            UidNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            UidNote.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Char_FgMuted" : "Char_Accent");
        }

        private string L(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
