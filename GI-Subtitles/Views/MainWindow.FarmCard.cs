using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;
using WinForms = System.Windows.Forms;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The Today card (every game; for Genshin it carries "what to farm today"): a watcher of its own (never the OCR
    /// loop) shows the card the first time the selected game is in front in a new game day; the tray, Settings ›
    /// Characters and Start show it on demand. Everything it shows is already on this PC or on kaption.one: the stamina
    /// timer's last reading, the manifest's reset rules, the site's week file, what the screen watch stored. A failed
    /// read or a bad file means a section less, or no card: Info/Warn logs, never a dialog.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly TimeSpan FarmWatchInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan FarmIconWait = TimeSpan.FromSeconds(4);
        private DispatcherTimer _farmWatch;
        private readonly GameProbe _farmProbe = new();
        private readonly FarmCardTrigger _farmTrigger = new();
        private FarmCardWindow _farmCard;
        private bool _farmProbing, _farmShowing;
        private string _farmWatchedGame;

        private void InitFarmCard()
        {
            _farmWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = FarmWatchInterval };
            _farmWatch.Tick += (_, __) => WatchFarmCard();
            _farmWatch.Start();
            Closed += (_, __) =>
            {
                _farmWatch?.Stop();
                _farmCard?.CloseNow();
            };
            if (data != null) data.OnShowFarmCard = ShowFarmCardOnDemand;
        }

        /// <summary>The tray's item, the Characters page's button and the Start block's button.</summary>
        public void ShowFarmCardOnDemand() => _ = ShowFarmCardAsync(onDemand: true);

        // Off the UI thread: the probe lists processes now and then.
        private async void WatchFarmCard()
        {
            if (_farmProbing || _farmShowing) return;
            _farmProbing = true;
            try
            {
                GameManifest game = TodayCardSettings.ActiveGame();
                // The game, server, saved UID and game day, read once: the probe below is a wait, and so is the build after it.
                TodayCardRequest request = TodayCardRequest.Capture(game, DateTimeOffset.UtcNow);
                string key = request.DayKey;
                // Another game was selected: the time the old one spent in front does not count for this one.
                if (!string.Equals(_farmWatchedGame, game?.Id, StringComparison.Ordinal))
                {
                    _farmWatchedGame = game?.Id;
                    _farmTrigger.Reset();
                }
                if (!FarmCardTrigger.ShouldWatch(FarmCardSettings.Enabled, TodayCardSettings.HasCard(game), TodayCardSettings.LastShownDay(game), key))
                {
                    _farmTrigger.Reset();
                    return;
                }
                var profile = GameRegionProfile.Get(game.GameId);
                string[] names = profile?.ProcessNames ?? Array.Empty<string>();
                // A generic executable (WuWa's Unreal shell) counts only with one of the game's window titles.
                string[] titles = profile?.RequiresWindowTitle == true ? profile.WindowTitles ?? Array.Empty<string>() : null;
                var probe = await Task.Run(() => _farmProbe.Probe(names, DateTime.UtcNow, titles));
                if (_farmTrigger.Tick(FarmCardSettings.Enabled, probe.Foreground, TodayCardSettings.LastShownDay(game), key, DateTime.UtcNow))
                {
                    // A card the player asked for started loading during the probe's wait: ShowFarmCardAsync would
                    // return at once, and the day must not be written for a showing that never runs. The next look
                    // comes back. Nothing waits between this check and that method taking the flag.
                    if (_farmShowing) return;
                    // Once per game day, even when this showing finds nothing to show.
                    string before = TodayCardSettings.LastShownDay(game);
                    TodayCardSettings.SetLastShownDay(game, key);
                    Logger.Log.Info($"Today card: {game.Id} in front in a new game day.");
                    await ShowFarmCardAsync(onDemand: false, request, before);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Today card watch failed: {ex.Message}");
            }
            finally
            {
                _farmProbing = false;
            }
        }

        /// <param name="request">
        /// What the card is for (the game, server, saved UID and game day, read once). The watcher passes the one it
        /// keyed "shown today" with; on demand it is read here.
        /// </param>
        /// <param name="dayBefore">
        /// For the showing by itself: what "last shown" was before this one, put back when the player selects another
        /// game or server, saves another UID, or the game day turns, while the card is being built. That card is then
        /// not shown, the day is not used up, and the watcher builds the right one on its next look.
        /// </param>
        private async Task ShowFarmCardAsync(bool onDemand, TodayCardRequest request = null, string dayBefore = null)
        {
            if (_farmShowing) return;
            _farmShowing = true;
            try
            {
                request ??= TodayCardRequest.Capture(TodayCardSettings.ActiveGame(), DateTimeOffset.UtcNow);
                GameManifest game = request.Game;
                var clock = Stopwatch.StartNew();
                string lang = UiLanguage.Current;
                long builtMs = 0;
                // By itself the card needs more than the reset countdowns; asked for, it shows whatever there is.
                bool Shows(TodayCardModel m) => m != null && (onDemand ? m.HasSections : m.WorthShowing);

                // Everything that waits, in one piece: the data, then for a card that will show its icons and the
                // screen it goes on. Whether the request still holds is asked after all of it (PrepareAndShowAsync),
                // with nothing but the window's creation between that answer and the card.
                async Task<(TodayCardModel Model, System.Drawing.Rectangle WorkArea, IReadOnlyList<Rect> Avoid)> Prepare(TodayCardRequest r)
                {
                    TodayCardModel m = await TodayCardLoader.BuildAsync(r, lang, TodayCardSources.Live);
                    builtMs = clock.ElapsedMilliseconds;
                    if (!Shows(m)) return (m, default, null);
                    if (m.Farm != null) await FetchFarmIconsAsync(m.Farm);
                    var (area, off) = await Task.Run(() => FarmCardScreen(game));
                    return (m, area, off);
                }

                // By itself: the game, the server, the saved UID and the game day must still be the selected ones,
                // else the card is dropped and the watcher comes back. Asked for by the player: the card stays the
                // game they asked about, and a server or UID picked or a reset passed while it loaded gets one fresh
                // build. The answer and the showing are one step (PrepareAndShowAsync): Present below waits for nothing.
                var attempt = await TodayCardLoader.PrepareAndShowAsync(request, Prepare,
                    r => r.StillCurrent(onDemand ? game : TodayCardSettings.ActiveGame(), TodayCardSettings.ServerId, FarmCardSettings.Uid,
                        DateTimeOffset.UtcNow),
                    () => TodayCardRequest.Capture(game, DateTimeOffset.UtcNow), rebuilds: onDemand ? 1 : 0,
                    (_, prepared) => Present(prepared.Model, prepared.WorkArea, prepared.Avoid));
                if (!attempt.Current)
                {
                    if (!onDemand) TodayCardSettings.SetLastShownDay(game, dayBefore);
                    Logger.Log.Info($"Today card: the game, the server, the UID or the game day changed while {game?.Id}'s card was being built "
                        + $"({attempt.Builds} build(s)); not shown.");
                }
                else if (attempt.Builds > 1)
                {
                    Logger.Log.Info($"Today card: the server, the UID or the game day changed while {game?.Id}'s card was being built; built again.");
                }

                // Synchronous on purpose: nothing may come between the freshness answer and the card.
                void Present(TodayCardModel model, System.Drawing.Rectangle workArea, IReadOnlyList<Rect> avoid)
                {
                    if (!Shows(model))
                    {
                        Logger.Log.Info($"Today card: nothing to show for {game?.Id ?? "this game"} today, so no card.");
                        if (onDemand) notify?.ShowBalloonTip(FarmL("TodayCard_Nothing_Title", "Nothing for today yet"),
                            FarmL("TodayCard_Nothing_Body", "The card fills in once Kaption has read your stamina in the game or can reach kaption.one."));
                        return;
                    }

                    var vm = new TodayCardViewModel(model, lang, FarmL);
                    _farmCard?.CloseNow();
                    var card = new FarmCardWindow(vm, workArea, avoid, GI_Subtitles.Themes.ThemeService.IsDark);
                    card.View.DontShowRequested += (_, __) =>
                    {
                        FarmCardSettings.Enabled = false;
                        Logger.Log.Info("Today card: turned off from the card.");
                        card.BeginFadeClose();
                        data?.RefreshFarmBlock();
                        data?.RefreshTodayBlock();
                    };
                    card.View.AddUidRequested += (_, __) =>
                    {
                        card.BeginFadeClose();
                        data?.ShowFarmBlockUid();
                    };
                    card.View.FullListRequested += (_, __) => OpenFarmLink(model.FullListUrl);
                    card.View.LinkRequested += OpenFarmLink;
                    card.Closed += (_, __) => { if (ReferenceEquals(_farmCard, card)) _farmCard = null; };
                    _farmCard = card;
                    card.Show();
                    // One showing's cost, for the owner (no analytics): gathering the data, then up to the window's Show.
                    Logger.Log.Info($"Today card shown for {model.Game} ({model.Weekday}; stamina {(model.Stamina != null ? "yes" : "no")}, "
                        + $"ending {model.Ending.Count}, seen {model.Seen.Count}, farm {(model.Farm != null ? model.Farm.Player.ToString() : "no")}); "
                        + $"built in {builtMs} ms, shown after {clock.ElapsedMilliseconds} ms.");
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Today card not shown: {ex.Message}");
            }
            finally
            {
                _farmShowing = false;
            }
        }

        /// <summary>Icons already on disk at once; the rest get a few seconds, then the card shows its glyphs.</summary>
        private static async Task FetchFarmIconsAsync(FarmCardModel model)
        {
            var cache = CharacterImageCache.ForGame(FarmCardSettings.GameId);
            var downloads = model.IconUrls().Where(u => cache.CachedPathOrNull(u) == null).Select(u => cache.GetAsync(u)).ToList();
            if (downloads.Count > 0) await Task.WhenAny(Task.WhenAll(downloads), Task.Delay(FarmIconWait));
            foreach (var b in model.Books) b.IconPath = cache.CachedPathOrNull(b.IconUrl);
            foreach (var r in model.Rows)
            {
                r.IconPath = cache.CachedPathOrNull(r.IconUrl);
                foreach (var s in r.Steps) s.IconPath = cache.CachedPathOrNull(s.IconUrl);
            }
        }

        /// <summary>
        /// The game's monitor (else the cursor's) and what the card stays off, physical pixels: the dialogue regions
        /// and the strip of the game's top bar where its stamina number sits.
        /// </summary>
        private (System.Drawing.Rectangle WorkArea, IReadOnlyList<Rect> Avoid) FarmCardScreen(GameManifest game)
        {
            WinForms.Screen screen = null;
            var avoid = new List<Rect>();
            try
            {
                var info = GameWindowDetector.FindGameWindow(GameRegionProfile.Get(game.GameId));
                if (info != null)
                {
                    screen = WinForms.Screen.FromPoint(new System.Drawing.Point(info.Value.GameX + info.Value.GameW / 2, info.Value.GameY + info.Value.GameH / 2));
                    Rect bar = StaminaBar(game, new Rect(info.Value.GameX, info.Value.GameY, info.Value.GameW, info.Value.GameH));
                    if (!bar.IsEmpty) avoid.Add(bar);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Today card: game window not found: {ex.Message}");
            }
            screen ??= WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            foreach (var r in new[] { notify?.Region, notify?.Region2 })
                if (TryRegionRect(r, out var rect)) avoid.Add(rect);
            return (screen.WorkingArea, avoid);
        }

        /// <summary>
        /// Where the game draws its stamina number, from the manifest's measured search strip (fractions of the game
        /// picture); empty for a game without one. The card must not sit on the number the reader looks for.
        /// </summary>
        internal static Rect StaminaBar(GameManifest game, Rect gameWindow)
        {
            var s = game?.Desktop?.StaminaScreen?.Search;
            if (s == null || gameWindow.IsEmpty || !(s.W > 0) || !(s.H > 0)) return Rect.Empty;
            return new Rect(gameWindow.X + s.X * gameWindow.Width, gameWindow.Y + s.Y * gameWindow.Height, s.W * gameWindow.Width, s.H * gameWindow.Height);
        }

        private static bool TryRegionRect(string[] region, out Rect rect)
        {
            rect = Rect.Empty;
            if (region == null || region.Length < 4) return false;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(region[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            if (v[2] <= 0 || v[3] <= 0) return false;
            rect = new Rect(v[0], v[1], v[2], v[3]);
            return true;
        }

        private static void OpenFarmLink(string url)
        {
            if (!CharacterPageLink.IsSitePage(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Log.Warn($"Today card: link not opened: {ex.Message}"); }
        }

        private string FarmL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
