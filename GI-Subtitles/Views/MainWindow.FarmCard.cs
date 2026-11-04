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
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;
using WinForms = System.Windows.Forms;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "What to farm today" (Genshin): a watcher of its own (never the OCR loop) shows the card the first time Genshin
    /// is in front in a new server day; the tray and Settings › Characters show it on demand. A failed read or a bad
    /// file means no card: Info/Warn logs, never a dialog.
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

        /// <summary>The tray's "What to farm today" and the Characters page's button.</summary>
        public void ShowFarmCardOnDemand() => _ = ShowFarmCardAsync(onDemand: true);

        // Off the UI thread: the probe lists processes now and then.
        private async void WatchFarmCard()
        {
            if (_farmProbing || _farmShowing) return;
            _farmProbing = true;
            try
            {
                var region = FarmCardSettings.Region;
                string key = ServerDay.At(DateTimeOffset.UtcNow, region).Key(region);
                if (!FarmCardTrigger.ShouldWatch(FarmCardSettings.Enabled, FarmCardSettings.IsActiveGame(), FarmCardSettings.LastShownDay, key))
                {
                    _farmTrigger.Reset();
                    return;
                }
                var profile = GameRegionProfile.Get(FarmCardSettings.GameId);
                string[] names = profile?.ProcessNames ?? Array.Empty<string>();
                var probe = await Task.Run(() => _farmProbe.Probe(names, DateTime.UtcNow));
                if (_farmTrigger.Tick(FarmCardSettings.Enabled, probe.Foreground, FarmCardSettings.LastShownDay, key, DateTime.UtcNow))
                {
                    // Once per server day, even when this showing finds nothing to show.
                    FarmCardSettings.LastShownDay = key;
                    Logger.Log.Info("Farm card: Genshin in front in a new server day.");
                    await ShowFarmCardAsync(onDemand: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card watch failed: {ex.Message}");
            }
            finally
            {
                _farmProbing = false;
            }
        }

        private async Task ShowFarmCardAsync(bool onDemand)
        {
            if (_farmShowing) return;
            _farmShowing = true;
            try
            {
                var region = FarmCardSettings.Region;
                var now = DateTimeOffset.UtcNow;
                var day = ServerDay.At(now, region);
                var schedule = await FarmTodayData.ScheduleAsync(day);
                if (schedule == null)
                {
                    Logger.Log.Info("Farm card: no domain schedule yet, so no card.");
                    if (onDemand) notify?.ShowBalloonTip(FarmL("FarmCard_Eyebrow", "What to farm today"),
                        FarmL("FarmBlock_Unavailable", "Today's books will show once Kaption can reach kaption.one."));
                    return;
                }
                string uid = FarmCardSettings.Uid;
                var read = await FarmTodayData.ShowcaseAsync(uid, day, region);
                var catalog = await CharacterCatalogService.GetAsync(FarmCardSettings.GameId);
                string lang = UiLanguage.Current;
                var model = FarmCardBuilder.Build(schedule, day, now, uid != null, read.Roster, catalog, lang, read.UnknownUid);
                await FetchFarmIconsAsync(model);

                var vm = new FarmCardViewModel(model, lang, FarmL);
                var (workArea, avoid) = await Task.Run(FarmCardScreen);
                _farmCard?.CloseNow();
                var card = new FarmCardWindow(vm, workArea, avoid, GI_Subtitles.Themes.ThemeService.IsDark);
                card.View.DontShowRequested += (_, __) =>
                {
                    FarmCardSettings.Enabled = false;
                    Logger.Log.Info("Farm card: turned off from the card.");
                    card.BeginFadeClose();
                    data?.RefreshFarmBlock();
                };
                card.View.AddUidRequested += (_, __) =>
                {
                    card.BeginFadeClose();
                    data?.ShowFarmBlockUid();
                };
                card.View.FullListRequested += (_, __) => OpenFarmLink(model.FullListUrl);
                card.Closed += (_, __) => { if (ReferenceEquals(_farmCard, card)) _farmCard = null; };
                _farmCard = card;
                card.Show();
                Logger.Log.Info($"Farm card shown ({model.Weekday}, {model.Books.Count} books, {model.Player}, {model.Rows.Count} rows).");
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card not shown: {ex.Message}");
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

        /// <summary>The game's monitor (else the cursor's) and the dialogue region the card stays off, physical pixels.</summary>
        private (System.Drawing.Rectangle WorkArea, IReadOnlyList<Rect> Avoid) FarmCardScreen()
        {
            WinForms.Screen screen = null;
            try
            {
                var info = GameWindowDetector.FindGameWindow(GameRegionProfile.Get(FarmCardSettings.GameId));
                if (info != null) screen = WinForms.Screen.FromPoint(new System.Drawing.Point(info.Value.GameX + info.Value.GameW / 2, info.Value.GameY + info.Value.GameH / 2));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Farm card: game window not found: {ex.Message}");
            }
            screen ??= WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            var avoid = new List<Rect>();
            foreach (var r in new[] { notify?.Region, notify?.Region2 })
                if (TryRegionRect(r, out var rect)) avoid.Add(rect);
            return (screen.WorkingArea, avoid);
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
            catch (Exception ex) { Logger.Log.Warn($"Farm card: link not opened: {ex.Message}"); }
        }

        private string FarmL(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
