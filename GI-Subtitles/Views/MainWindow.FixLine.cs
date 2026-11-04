using System;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Translation;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>"Fix this line": the last shown line, the fix layers over the pack, and the fix window (hotkey 9007, tray).</summary>
    public partial class MainWindow
    {
        private const int HOTKEY_ID_8 = HotkeySettingsManager.FixLineHotkeyId;

        private readonly LastShownLineTracker _lastShownLine = new LastShownLineTracker();
        private TranslationLayers _layers;
        private OptimizedMatcher _layersMatcher;
        private SuggestFixWindow _suggestFixWindow;
        private bool _tellingNothingToFix;

        /// <summary>The last line painted (tests record lines here to drive the hotkey).</summary>
        internal LastShownLineTracker LastShownLine => _lastShownLine;

        /// <summary>The fix window now open, or null.</summary>
        internal SuggestFixWindow OpenFixWindow => _suggestFixWindow;

        /// <summary>Shows the "nothing to fix yet" notice (title, body). Null shows the dialog; tests capture it here.</summary>
        internal Action<string, string> NothingToFixNotice { get; set; }

        /// <summary>
        /// The fixes for the game and language in use. A language or game reload replaces the matcher, so the registry
        /// is asked again only then; every other tick is one reference compare.
        /// </summary>
        private TranslationLayers LayersFor(OptimizedMatcher matcher)
        {
            if (_layers == null || !ReferenceEquals(matcher, _layersMatcher))
            {
                var next = TranslationLayerRegistry.For(Config.Get("Game", "Genshin"), Config.Get("Output", "PL"));
                // Another language (or a startup that was offline): fetch its community fixes, at most every 30 min.
                if (!ReferenceEquals(next, _layers))
                    CommunitySync.RefreshIfStale(next, () => App.LicenseService?.CurrentActivation?.DeviceSessionJwt);
                _layers = next;
                _layersMatcher = matcher;
            }
            return _layers;
        }

        /// <summary>Hot path: no allocation (see <see cref="LastShownLineTracker"/>).</summary>
        private void RememberShownLine(TranslationLayers layers, string enKey, string shown, string pack) =>
            _lastShownLine.Record(layers.Game, layers.Lang, enKey, shown, pack, _detectedNpcName, DateTime.UtcNow);

        /// <summary>Opens the fix window for the last shown line, or says there is none yet.</summary>
        /// <param name="fromHotkey">The game has focus: it gets it back when the window closes.</param>
        internal void OpenSuggestFix(bool fromHotkey)
        {
            try
            {
                bool hasLine = _lastShownLine.TryGet(out var line);
                var open = _suggestFixWindow;
                if (open != null)
                {
                    // A newer line replaces an untouched window; an edit in progress is never thrown away.
                    if (!hasLine || line.EnKey == open.Line.EnKey || !open.IsUntouched)
                    {
                        open.Activate();
                        return;
                    }
                    _suggestFixWindow = null;
                    open.CloseForNewerLine();
                }
                IntPtr returnTo = fromHotkey ? GetForegroundWindow() : IntPtr.Zero;
                if (!hasLine)
                {
                    TellNothingToFix(returnTo);
                    return;
                }
                var layers = TranslationLayerRegistry.For(line.Game, line.Lang);
                if (!layers.IsLoaded) layers.EnsureLoaded(); // normally done in the background already
                var window = new SuggestFixWindow(line, layers, SuggestionSubmitter.Shared, returnTo);
                window.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_suggestFixWindow, s)) _suggestFixWindow = null;
                };
                _suggestFixWindow = window;
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"Fix this line: the window could not open: {ex}");
            }
        }

        // A dialog, not a balloon: Windows holds notifications back while a game runs full screen. The game gets
        // focus back afterwards; a second press while the dialog is up does not stack another one.
        private void TellNothingToFix(IntPtr returnTo)
        {
            if (_tellingNothingToFix) return;
            var (title, body) = NothingToFixText(
                HotkeySettingsManager.DescribeBinding(HotkeySettingsManager.FixLineHotkeyId), LocalizedString);
            _tellingNothingToFix = true;
            try
            {
                if (NothingToFixNotice != null) NothingToFixNotice(title, body);
                else ModernDialog.Info(owner: null, title: title, body: body);
            }
            finally
            {
                _tellingNothingToFix = false;
            }
            RestoreForeground(returnTo);
        }

        /// <summary>The "nothing to fix yet" message: names the shortcut when it is bound, else the tray entry.</summary>
        internal static (string Title, string Body) NothingToFixText(string combo, Func<string, string, string> localize)
        {
            string title = localize("fix_Nothing_Title", "No line to fix yet");
            string body = combo == null
                ? localize("fix_Nothing_Body_Tray",
                    "Start translating. When a subtitle you want to fix is on screen, right-click the Kaption icon by the clock and pick Fix the last subtitle.")
                : SuggestFixWindow.FormatOr(localize("fix_Nothing_Body", "Start translating. When a subtitle you want to fix is on screen, press {0}."), combo);
            return (title, body);
        }
    }
}
