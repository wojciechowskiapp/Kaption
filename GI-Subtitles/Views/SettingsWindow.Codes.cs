using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Views.Shell;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Start › the codes card: the selected game's active codes from api.kaption.one, read by <see cref="CodesPoller"/>
    /// off the UI thread, only while this window is on screen. The UI thread only applies the result.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>The player folded the codes list (the header stays).</summary>
        public const string CodesCardFoldedKey = "Ui.CodesCard.Folded";

        private CodesPoller _codesPoller;
        /// <summary>One-shot: rebuilds the card when its list goes stale or a shown code expires, read or no read.</summary>
        private DispatcherTimer _codesDeadline;
        private volatile bool _codesWindowOnScreen;

        private void InitializeCodesCard()
        {
            if (CodesCard == null) return;
            CodesCard.OpenUrl += OpenUrlInBrowser;
            CodesCard.FoldChanged += folded => Config.Set(CodesCardFoldedKey, folded);

            var api = new KaptionApiClient();
            var schedule = CodesSchedule.Resolve(CodesSchedulePins.FromConfig());
            _codesPoller = new CodesPoller(api.GetRedeemCodesAsync, CodesGameWireId, () => _codesWindowOnScreen, () => schedule);
            _codesPoller.ListRead += _ => PostCodesCardUpdate();
            _codesPoller.ReadFailed += _ => PostCodesCardUpdate();

            IsVisibleChanged += (_, __) => OnCodesWindowShown();
            StateChanged += (_, __) => OnCodesWindowShown();
            UiLanguage.Changed += OnCodesLanguageChanged;
            Loaded += (_, __) =>
            {
                _codesWindowOnScreen = IsVisible && WindowState != WindowState.Minimized;
                _codesPoller.Start();
            };
            _codesDeadline = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _codesDeadline.Tick += (_, __) =>
            {
                _codesDeadline.Stop();
                ApplyCodesCard();
            };
            Closed += (_, __) =>
            {
                _codesDeadline.Stop();
                UiLanguage.Changed -= OnCodesLanguageChanged;
                _codesPoller.Dispose();
            };
        }

        /// <summary>The selected game's wire id when kaption.one has a codes page for it; null otherwise (nothing is read).</summary>
        private static string CodesGameWireId()
        {
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            return CodesLinks.HasCodesPage(game) ? game.Id : null;
        }

        private void OnCodesWindowShown()
        {
            _codesWindowOnScreen = IsVisible && WindowState != WindowState.Minimized;
            if (_codesWindowOnScreen)
            {
                ApplyCodesCard();               // drops what went stale or expired while the window was away
                _codesPoller?.RequestRefresh();
            }
            else
            {
                _codesDeadline?.Stop();         // nobody sees the card; showing the window rebuilds it
            }
        }

        private void OnCodesLanguageChanged(object sender, EventArgs e) => PostCodesCardUpdate();

        /// <summary>After a game switch: the new game's cached list at once, a read when it is old or missing.</summary>
        private void RefreshCodesCard()
        {
            ApplyCodesCard();
            _codesPoller?.RequestRefresh();
        }

        // BeginInvoke, not Invoke: it never throws once the dispatcher has shut down.
        private void PostCodesCardUpdate()
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(ApplyCodesCard));
        }

        private void ApplyCodesCard()
        {
            if (CodesCard == null || _codesPoller == null) return;
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            CodesSnapshot last = _codesPoller.Last(game?.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan staleAfter = _codesPoller.Schedule.StaleAfter;
            CodesCardView view = CodesCardModel.Build(game, last?.List, last?.ReadAt, now, staleAfter, UiLanguage.Current,
                CultureInfo.CurrentUICulture, words: GameCodesCard.DateWords(L));
            CodesCard.Apply(view, Config.Get(CodesCardFoldedKey, false), L, CultureInfo.CurrentUICulture);

            _codesDeadline?.Stop();
            DateTimeOffset? next = CodesCardModel.NextChangeAt(game, last?.List, last?.ReadAt, now, staleAfter);
            if (next.HasValue && _codesWindowOnScreen && _codesDeadline != null)
            {
                _codesDeadline.Interval = CodesCardModel.WaitUntil(next.Value, now);
                _codesDeadline.Start();
            }
        }
    }
}
