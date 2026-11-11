using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Guess;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Themes;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The guess game in Settings: the Start page's "Nowość w Kaption" card (Wuthering Waves in Polish, the guess game),
    /// the strip that shows a ranking notice's text, and the Help page's row with the notice switch. Nothing here polls;
    /// MainWindow.GuessNotify.cs owns the standing reads and hands the notice over.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>The player opened the ranking from the strip: MainWindow starts the baselines again from now.</summary>
        public Action GuessLobbyOpened;

        private SpotlightRows _spotlightRows = SpotlightRows.None;
        private bool _spotlightCounted;
        private bool _syncingGuessToggle;
        private bool _onboardingRanThisLaunch;

        private void InitializeGuessNotify()
        {
            _onboardingRanThisLaunch = App.FirstRunOutcome != OnboardingWindow.Outcome.None;
            SyncGuessNotifyToggle();
            if (GuessCommunityBody != null)
                GuessCommunityBody.Text = GuessCommunityText(L);
            GuessNotifySettings.EnabledChanged += OnGuessNotifyEnabledChanged;
            Loaded += (_, __) => _ = ShowSpotlightAsync();
            Closed += (_, __) => GuessNotifySettings.EnabledChanged -= OnGuessNotifyEnabledChanged;
        }

        /// <summary>"Gendle, Stardle, Zendle and Wuwdle: …", from the listed games' manifests.</summary>
        internal static string GuessCommunityText(Func<string, string, string> L)
        {
            string names = GuessNoticeText.JoinNames(GuessNoticeText.AllGuessNames(), " " + L("shell_Guess_And", "and").Trim() + " ");
            string format = L("shell_Guess_Body_Format", "{0}: a new puzzle every day and a ranked leaderboard each month.");
            try { return string.Format(CultureInfo.CurrentCulture, format, names); }
            catch (FormatException) { return names; }
        }

        // ── Help: the switch ─────────────────────────────────────────────

        private void OnGuessNotifyEnabledChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnGuessNotifyEnabledChanged));
                return;
            }
            SyncGuessNotifyToggle();
            if (!GuessNotifySettings.Enabled) ShowGuessRankNotice(null);
        }

        private void SyncGuessNotifyToggle()
        {
            if (GuessNotifyToggle == null) return;
            _syncingGuessToggle = true;
            try { GuessNotifyToggle.IsChecked = GuessNotifySettings.Enabled; }
            finally { _syncingGuessToggle = false; }
        }

        // XAML's IsChecked="True" fires this while the window is built: only the player's own change writes.
        private void GuessNotifyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _syncingGuessToggle) return;
            bool on = GuessNotifyToggle.IsChecked == true;
            GuessNotifySettings.Enabled = on;
            Logger.Log.Info($"Guess ranking notices turned {(on ? "on" : "off")} on Help.");
        }

        private void GuessPlay_Click(object sender, RoutedEventArgs e) => OpenUrlInBrowser(GuessLinks.Play(UiLanguage.Current));

        // ── Start: the ranking strip ─────────────────────────────────────

        /// <summary>Shows a ranking notice's text on Start (null hides it). Safe from any thread.</summary>
        public void ShowGuessRankNotice(GuessNotice notice)
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => ShowGuessRankNotice(notice)));
                return;
            }
            ApplyGuessRankStripTo(FindName, notice);
        }

        /// <summary>The strip for a notice, or hidden for none. Static so the render tests run the app's own code.</summary>
        internal static bool ApplyGuessRankStripTo(Func<string, object> find, GuessNotice notice)
        {
            if (!(find("GuessRankStrip") is UIElement strip)) return false;
            bool show = notice != null && !string.IsNullOrWhiteSpace(notice.Body);
            if (show && find("GuessRankStripText") is TextBlock text) text.Text = notice.Body;
            strip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return show;
        }

        private void GuessRankOpen_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser(GuessLinks.RankedLobby(UiLanguage.Current));
            GuessLobbyOpened?.Invoke();
            ShowGuessRankNotice(null);
        }

        private void GuessRankTurnOff_Click(object sender, RoutedEventArgs e)
        {
            GuessNotifySettings.Enabled = false; // the switch on Help and the poller follow through EnabledChanged
            Logger.Log.Info("Guess ranking notices turned off from the Start strip.");
            ShowGuessRankNotice(null);
        }

        // ── Start: "Nowość w Kaption" ────────────────────────────────────

        private async Task ShowSpotlightAsync()
        {
            try
            {
                var packs = await DashboardFeeds.LatestPacksAsync().ConfigureAwait(true);
                bool listed = SpotlightRules.HasWuWaPolish(packs, out string version);
                string game = Config.Get("Game", "Genshin") ?? "Genshin";
                string wuwaId = GameRegistry.Current.FindByWireId("wuwa")?.GameId ?? "WuWa";
                var rows = SpotlightRules.Rows(SpotlightRules.FromConfig(new SpotlightInputs
                {
                    SetupCompleted = Config.Get("SetupCompleted", false),
                    OnboardingRanThisLaunch = _onboardingRanThisLaunch,
                    Intro25Visible = Intro25Card?.Visibility == Visibility.Visible,
                    WuWaPolishListed = listed,
                    WuWaComing = PackAvailability.IsComing(wuwaId, "pl"),
                    WuWaGameVersion = version,
                    WuWaSelected = string.Equals(GameRegionProfile.Get(game).GameId, wuwaId, StringComparison.Ordinal),
                }));
                ImageSource wuwaArt = rows.WuWa ? await AmbienceArt.LoadAsync(AmbienceArt.ForGame(wuwaId)).ConfigureAwait(true) : null;
                ImageSource guessArt = rows.Guess ? await AmbienceArt.LoadAsync(AmbienceArt.ForGame(game)).ConfigureAwait(true) : null;
                string guessName = GuessNoticeText.GuessNameOf(GameRegistry.Current.Find(game)?.Id) ?? "Gendle";
                ApplySpotlightTo(FindName, rows, guessName, wuwaArt, guessArt, L, AccentOf(wuwaId), AccentOf(game));
                _spotlightRows = rows;
                if (rows.Any && !_spotlightCounted)
                {
                    _spotlightCounted = true; // once per launch, whatever reruns this
                    SpotlightRules.CountShown(rows);
                    Logger.Log.Info($"Start spotlight shown (WuWa {rows.WuWa}, guess {rows.Guess}).");
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Start spotlight skipped: {ex.Message}");
            }
        }

        private Brush AccentOf(string game) =>
            TryFindResource(AccentKeyFor(game)) as Brush ?? new SolidColorBrush(GameLook.AccentOf(game));

        /// <summary>
        /// Fills the spotlight card for <paramref name="rows"/>; hidden when no row is left. Static so the render tests
        /// run the app's own code (the art comes in decoded: the window loads it from the app's resources).
        /// </summary>
        internal static bool ApplySpotlightTo(Func<string, object> find, SpotlightRows rows, string guessName, ImageSource wuwaArt,
                                              ImageSource guessArt, Func<string, string, string> L, Brush wuwaAccent, Brush guessAccent)
        {
            rows ??= SpotlightRows.None;
            if (!(find("SpotlightCard") is UIElement card)) return false;
            if (find("SpotlightWuWaRow") is FrameworkElement wuwaRow)
            {
                wuwaRow.Visibility = rows.WuWa ? Visibility.Visible : Visibility.Collapsed;
                if (find("SpotlightWuWaBody") is TextBlock body)
                    body.Text = string.IsNullOrWhiteSpace(rows.WuWaVersion)
                        ? L("shell_Spotlight_WuWa_Body", "The subtitles are ready. Pick the game and they download on their own.")
                        : SafeFormat(L("shell_Spotlight_WuWa_Body_Format", "Version {0} is ready. Pick the game and the subtitles download on their own."),
                            "Version {0} is ready. Pick the game and the subtitles download on their own.", rows.WuWaVersion);
                if (find("SpotlightWuWaActionButton") is Button action)
                    action.Content = rows.WuWaAction == SpotlightWuWaAction.OpenGamePage
                        ? L("shell_Spotlight_WuWa_Open", "Open Game and subtitles")
                        : L("shell_Spotlight_WuWa_Switch", "Switch to Wuthering Waves");
                Thumb(find("SpotlightWuWaThumb") as Border, wuwaArt, wuwaAccent);
            }
            if (find("SpotlightGuessRow") is FrameworkElement guessRow)
            {
                guessRow.Visibility = rows.Guess ? Visibility.Visible : Visibility.Collapsed;
                guessRow.Margin = new Thickness(0, rows.WuWa && rows.Guess ? 14 : 0, 0, 0);
                string title = SafeFormat(L("shell_Spotlight_Guess_Title_Format", "{0}: guess today's character"),
                    "{0}: guess today's character", guessName);
                if (find("SpotlightGuessTitle") is TextBlock t) t.Text = title;
                if (find("SpotlightGuessThumb") is Border thumb)
                {
                    Thumb(thumb, guessArt, guessAccent);
                    System.Windows.Automation.AutomationProperties.SetName(thumb, title);
                }
            }
            card.Visibility = rows.Any ? Visibility.Visible : Visibility.Collapsed;
            return rows.Any;
        }

        private static void Thumb(Border thumb, ImageSource art, Brush accent)
        {
            if (thumb == null) return;
            if (art != null) thumb.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center };
            if (accent != null) thumb.BorderBrush = accent;
        }

        private static string SafeFormat(string format, string fallback, string arg)
        {
            try { return string.Format(CultureInfo.CurrentCulture, format, arg); }
            catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, fallback, arg); }
        }

        private void RetireSpotlightRow(bool wuwa, bool guess)
        {
            SpotlightRules.MarkSeen(wuwa, guess);
            _spotlightRows = _spotlightRows with
            {
                WuWa = _spotlightRows.WuWa && !wuwa,
                Guess = _spotlightRows.Guess && !guess,
            };
            if (SpotlightWuWaRow != null && wuwa) SpotlightWuWaRow.Visibility = Visibility.Collapsed;
            if (SpotlightGuessRow != null)
            {
                if (guess) SpotlightGuessRow.Visibility = Visibility.Collapsed;
                SpotlightGuessRow.Margin = new Thickness(0);
            }
            if (SpotlightCard != null && !_spotlightRows.Any) SpotlightCard.Visibility = Visibility.Collapsed;
        }

        private void SpotlightWuWa_Click(object sender, RoutedEventArgs e)
        {
            var action = _spotlightRows.WuWaAction;
            RetireSpotlightRow(wuwa: true, guess: false);
            if (action == SpotlightWuWaAction.Switch) SwitchGame(GameRegistry.Current.FindByWireId("wuwa")?.GameId ?? "WuWa");
            else SelectTabByTag(SettingsNavigation.Game);
        }

        private void SpotlightWuWaClose_Click(object sender, RoutedEventArgs e) => RetireSpotlightRow(wuwa: true, guess: false);

        private void SpotlightGuess_Click(object sender, RoutedEventArgs e)
        {
            RetireSpotlightRow(wuwa: false, guess: true);
            OpenUrlInBrowser(GuessLinks.Play(UiLanguage.Current));
        }

        private void SpotlightGuessClose_Click(object sender, RoutedEventArgs e) => RetireSpotlightRow(wuwa: false, guess: true);
    }
}
