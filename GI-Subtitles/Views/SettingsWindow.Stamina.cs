using System;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Start › the stamina card: the selected game's timer, kept by the number the capture loop reads off the game
    /// screen once the player switches that on here (MainWindow.Stamina.cs, Services/Stamina). The card reads Config
    /// only; it draws itself again every half minute while the window is on screen, because its texts count minutes.
    /// </summary>
    public partial class SettingsWindow
    {
        private DispatcherTimer _staminaCardClock;

        private void InitializeStaminaCard()
        {
            if (StaminaCard == null) return;
            StaminaCard.OpenUrl += OpenUrlInBrowser;
            StaminaCard.ReadChanged += on =>
            {
                StaminaSettings.Enabled = on;
                Logger.Log.Info($"Stamina reading from the screen turned {(on ? "on" : "off")} on Start.");
            };
            StaminaCard.NoticeChanged += on => StaminaSettings.Notice = on;
            StaminaSettings.Changed += RefreshStaminaCard;
            UiLanguage.Changed += OnStaminaLanguageChanged;

            _staminaCardClock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StaminaCardModel.RefreshEvery };
            _staminaCardClock.Tick += (_, __) => ApplyStaminaCard();
            IsVisibleChanged += (_, __) => OnStaminaWindowShown();
            StateChanged += (_, __) => OnStaminaWindowShown();
            Loaded += (_, __) => OnStaminaWindowShown();
            Closed += (_, __) =>
            {
                _staminaCardClock.Stop();
                StaminaSettings.Changed -= RefreshStaminaCard;
                UiLanguage.Changed -= OnStaminaLanguageChanged;
            };
        }

        private void OnStaminaLanguageChanged(object sender, EventArgs e) => RefreshStaminaCard();

        private void OnStaminaWindowShown()
        {
            bool onScreen = IsVisible && WindowState != WindowState.Minimized;
            if (onScreen)
            {
                ApplyStaminaCard();
                _staminaCardClock?.Start();
            }
            else
            {
                _staminaCardClock?.Stop(); // nobody sees the card; showing the window draws it again
            }
        }

        /// <summary>Draws the card again: after a new reading, a switch, a game or a language change. Safe from any thread.</summary>
        public void RefreshStaminaCard()
        {
            if (Dispatcher.HasShutdownStarted) return;
            // BeginInvoke, not Invoke: it never throws once the dispatcher has shut down.
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(ApplyStaminaCard));
            else ApplyStaminaCard();
        }

        private void ApplyStaminaCard()
        {
            if (StaminaCard == null) return;
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            StaminaCard.Apply(StaminaCardModel.Build(game, StaminaSettings.Enabled, StaminaSettings.Notice, StaminaSettings.NoticeLead,
                StaminaSettings.LoadReading(game?.Id), StaminaSettings.LoadSeen(game?.Id), DateTimeOffset.UtcNow, UiLanguage.Current, L,
                CultureInfo.CurrentUICulture));
        }
    }
}
