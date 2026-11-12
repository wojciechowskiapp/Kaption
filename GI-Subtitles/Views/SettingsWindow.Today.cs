using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Start › the Today block: the Today card's model for the selected game, so a player who never sees the card over
    /// the game still finds today's state in the app. Built again when the window shows, the game or the language
    /// changes, a stamina reading lands, and every <see cref="TodayBlockRefreshEvery"/> while it is on screen.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>Its countdowns are in hours and minutes; ten minutes keeps "in 5 h" honest without any work to speak of.</summary>
        private static readonly TimeSpan TodayBlockRefreshEvery = TimeSpan.FromMinutes(10);

        private DispatcherTimer _todayBlockClock;
        private int _todayBlockBuild;

        private void InitializeTodayBlock()
        {
            if (TodayBlock == null) return;
            TodayBlock.OpenUrl += url =>
            {
                if (CharacterPageLink.IsSitePage(url)) OpenUrlInBrowser(url);
            };
            TodayBlock.ShowCardRequested += () => OnShowFarmCard?.Invoke();
            TodayBlock.ServerChanged += id =>
            {
                TodayCardSettings.SetServer(id);
                Logger.Log.Info($"Today card: server set to {id} on Start.");
                RefreshTodayBlock();
                RefreshFarmBlock(); // the Characters page shows the same choice
            };
            StaminaSettings.Changed += RefreshTodayBlock;
            UiLanguage.Changed += OnTodayLanguageChanged;
            _todayBlockClock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TodayBlockRefreshEvery };
            _todayBlockClock.Tick += (_, __) => RefreshTodayBlock();
            IsVisibleChanged += (_, __) => OnTodayWindowShown();
            StateChanged += (_, __) => OnTodayWindowShown();
            Loaded += (_, __) => OnTodayWindowShown();
            Closed += (_, __) =>
            {
                _todayBlockClock.Stop();
                StaminaSettings.Changed -= RefreshTodayBlock;
                UiLanguage.Changed -= OnTodayLanguageChanged;
            };
        }

        private void OnTodayLanguageChanged(object sender, EventArgs e) => RefreshTodayBlock();

        private void OnTodayWindowShown()
        {
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                RefreshTodayBlock();
                _todayBlockClock?.Start();
            }
            else
            {
                _todayBlockClock?.Stop(); // nobody sees the block; showing the window builds it again
            }
        }

        /// <summary>Builds the block again. Safe from any thread; a slower, older build never overwrites a newer one.</summary>
        public void RefreshTodayBlock()
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshTodayBlock));
                return;
            }
            _ = ApplyTodayBlockAsync();
        }

        private async Task ApplyTodayBlockAsync()
        {
            if (TodayBlock == null) return;
            int build = ++_todayBlockBuild;
            try
            {
                GameManifest game = TodayCardSettings.ActiveGame();
                string lang = UiLanguage.Current;
                TodayCardRequest request = TodayCardRequest.Capture(game, DateTimeOffset.UtcNow);
                TodayCardModel model = TodayCardSettings.HasCard(game)
                    ? await TodayCardLoader.BuildAsync(request, lang, TodayCardSources.Live)
                    : null;
                if (build != _todayBlockBuild) return;
                // The game, the server, the saved UID or the game day changed while it loaded (the server and the UID
                // are set on the Characters page too): this build is not laid out, a fresh one is.
                if (!request.StillCurrent(TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId, FarmCardSettings.Uid, DateTimeOffset.UtcNow))
                {
                    RefreshTodayBlock();
                    return;
                }
                TodayBlock.Apply(model == null ? null : new TodayCardViewModel(model, lang, L),
                    string.Format(CultureInfo.CurrentUICulture, L("TodayCard_Eyebrow_Format", "Today in {0}"), game?.Name ?? ""));
                TodayBlock.SetServers(TodayCardSettings.Servers(game, L), game?.Resets?.Server(TodayCardSettings.ServerId)?.Id);
            }
            catch (Exception ex)
            {
                // A block less on Start is the whole cost: never a dialog.
                Logger.Log.Warn($"Today block not built: {ex.Message}");
                if (build == _todayBlockBuild) TodayBlock.Apply(null, null);
            }
        }
    }
}
