using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Wygląd napisów: the miniature of the user's real screen (and the 1:1 strip), the three looks, size and place, and
    /// "Przywróć domyślne". Every change goes to Config through <see cref="OverlayStyle"/> and straight to the running
    /// overlay; nothing is written while the window is built.
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _syncingLook;
        /// <summary>The open game's window (physical pixels) when the page last looked; null: the monitor is the frame.</summary>
        private Rect? _lookGameWindow;
        private bool _lookGameWindowBusy;

        private void InitializeLookPage()
        {
            if (LookPreview == null) return;
            LookPreview.PositionChanged += LookPreview_PositionChanged;
            SyncLookPage();
            _ = LoadLookArtAsync();
            // The frame follows the game window when it is open: look again whenever the page shows.
            NavTabs.SelectionChanged += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, NavTabs) && (NavTabs.SelectedItem as TabItem)?.Tag as string == SettingsNavigation.Look)
                    _ = RefreshLookGameWindowAsync();
            };
        }

        private async Task RefreshLookGameWindowAsync()
        {
            if (_lookGameWindowBusy) return;
            _lookGameWindowBusy = true;
            try
            {
                string game = Config.Get("Game", "Genshin");
                var found = await Task.Run(() => LookGeometry.FindGameWindow(game)).ConfigureAwait(true);
                if (found != _lookGameWindow)
                {
                    _lookGameWindow = found;
                    SyncLookPage();
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Look preview: game window lookup skipped: {ex.Message}");
            }
            finally
            {
                _lookGameWindowBusy = false;
            }
        }

        /// <summary>Miniature, 1:1 strip, looks and switches from the saved style (never writes).</summary>
        private void SyncLookPage()
        {
            if (LookPreview == null) return;
            _syncingLook = true;
            try
            {
                string game = Config.Get("Game", "Genshin");
                var look = OverlayStyle.FromConfig();
                var scene = LookGeometry.Compute(Config.Get<string>("Region", ""), Scale, game, _lookGameWindow, L);
                ApplyLookTo(FindName, look, game, scene, GameRegionProfile.ForCurrentGame().SubtitlePadVertical,
                    Config.Get("MaxOverlayWidth", SubtitleOverlayDefaults.MaxOverlayWidth), art: null, L);
                SyncRecordingCard();
            }
            finally
            {
                _syncingLook = false;
            }
        }

        private async Task LoadLookArtAsync()
        {
            try
            {
                var art = await AmbienceArt.LoadAsync(AmbienceArt.ForGame(Config.Get("Game", "Genshin"))).ConfigureAwait(true);
                if (art == null) return;
                if (LookPreview != null) LookPreview.Art = art;
                if (LookTrueSize != null) LookTrueSize.Art = art;
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Look preview art skipped: {ex.Message}");
            }
        }

        /// <summary>The game's own sample line (look_Sample_&lt;GameId&gt;_Speaker / _Line) when its strings exist, else the default.</summary>
        internal static (string Speaker, string Line) LookSample(string game, Func<string, string, string> L)
        {
            var registry = GI_Subtitles.Services.Games.GameRegistry.Current;
            string id = (registry.FindLoose(game) ?? registry.Default)?.GameId;
            return (L($"look_Sample_{id}_Speaker", null) ?? L("look_Sample_Speaker", "Paimon"),
                    L($"look_Sample_{id}_Line", null) ?? L("look_Sample_Line", "Ej, poczekaj!"));
        }

        /// <summary>
        /// Fills the page's named elements for a style and a scene: the miniature (the screen, the dialogue area, the
        /// panel where the app puts it), the 1:1 strip, the game's sample line and art, which look card is chosen, the
        /// darkening switch. Static so the render tests run the app's code on the loose XAML.
        /// </summary>
        internal static void ApplyLookTo(Func<string, object> find, OverlayStyle look, string game, LookScene scene, int defaultPad,
                                         int maxOverlayWidth, ImageSource art, Func<string, string, string> L)
        {
            var (speaker, line) = LookSample(game, L);
            double textWidth = LookGeometry.TextWidth(scene, maxOverlayWidth);
            if (find("LookPreview") is SubtitlePreview preview)
            {
                preview.Scene = scene;
                preview.DefaultPad = defaultPad;
                preview.TextWidth = textWidth;
                preview.Speaker = speaker;
                preview.Line = line;
                if (art != null) preview.Art = art;
                preview.Look = look;
                System.Windows.Automation.AutomationProperties.SetName(preview, L("look_Preview_Help", "Subtitle preview") + " " + scene.Label);
            }
            if (find("LookTrueSize") is SubtitleTrueSize trueSize)
            {
                trueSize.TextWidth = textWidth;
                trueSize.Speaker = speaker;
                trueSize.Line = line;
                if (art != null) trueSize.Art = art;
                trueSize.Look = look;
            }
            var match = look.MatchingPreset();
            foreach (var (name, preset) in new[] { ("PresetClassic", OverlayPreset.Classic), ("PresetContrast", OverlayPreset.Contrast), ("PresetCinema", OverlayPreset.Cinema) })
                if (find(name) is RadioButton card) card.IsChecked = match == preset;
            foreach (string swatch in new[] { "SwatchClassic", "SwatchContrast", "SwatchCinema" })
                if (find(swatch) is PresetSwatch s) s.Face = look.FontFamily;
            if (find("AutoDarkenToggle") is CheckBox darken) darken.IsChecked = look.AutoDarken;
            if (find("KaptionMarkToggle") is CheckBox mark) mark.IsChecked = look.Mark;
        }

        /// <summary>
        /// A look's card: its size, panel, outline and spacing. Kino also moves the subtitle low and centred, and the
        /// position from before Kino comes back when another look is picked.
        /// </summary>
        private void LookPreset_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingLook || !IsInitialized || !(sender is RadioButton rb) || !Enum.TryParse(rb.Tag as string, out OverlayPreset preset)) return;
            var saved = OverlayStyle.FromConfig();
            OverlayStyle next;
            if (preset == OverlayPreset.Cinema)
            {
                if (saved.MatchingPreset() != OverlayPreset.Cinema)
                {
                    if (saved.PadVertical.HasValue) Config.Set(PadBeforeCinemaKey, new[] { saved.PadVertical.Value, saved.PadHorizontal });
                    else Config.Remove(PadBeforeCinemaKey);
                }
                double reference = SubtitleCard.ReferenceHeight(saved.WithPreset(preset)) + OverlayStyle.WindowMargin * 2;
                next = saved.WithPreset(preset, reference);
            }
            else
            {
                next = saved.WithPreset(preset);
                if (saved.MatchingPreset() == OverlayPreset.Cinema)
                {
                    // Leaving Kino: the subtitle goes back where it was before.
                    var before = Config.Get<int[]>(PadBeforeCinemaKey, null);
                    next = before is { Length: 2 }
                        ? next with { PadVertical = before[0], PadHorizontal = before[1] }
                        : next with { PadVertical = null, PadHorizontal = 0 };
                    Config.Remove(PadBeforeCinemaKey);
                }
            }
            next.Save(saved);
            Logger.Log.Info($"Subtitle look: {preset} (size {next.Size}, panel {next.BackgroundOpacity}, outline {next.Outline}).");
            OverlayLookChanged();
        }

        /// <summary>The position Kino replaced, so picking another look puts the subtitle back.</summary>
        internal const string PadBeforeCinemaKey = "Overlay.PadBeforeCinema";

        private void LookReset_Click(object sender, RoutedEventArgs e)
        {
            OverlayStyle.ResetConfig();
            Config.Remove(PadBeforeCinemaKey);
            Logger.Log.Info("Subtitle look: restored the defaults.");
            OverlayLookChanged();
        }

        private void LookPreview_PositionChanged(int padVertical, int padHorizontal)
        {
            Config.Set(OverlayStyle.PadKey, new[] { padVertical, padHorizontal });
            OverlayLookChanged();
        }

        private void AutoDarkenToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingLook || !IsInitialized) return;
            Config.Set(OverlayStyle.AutoDarkenKey, AutoDarkenToggle.IsChecked == true);
            OverlayLookChanged();
        }

        /// <summary>
        /// The recordings switch and the line under it: what a screen recording shows on this PC right now, as the
        /// running overlay knows it (the setting alone while the overlay is not up). Never writes.
        /// </summary>
        private void SyncRecordingCard()
        {
            if (ShowInRecordingsToggle == null) return;
            bool show = CaptureVisibility.ShowInRecordings;
            var status = System.Windows.Application.Current?.MainWindow is MainWindow overlay
                ? overlay.CaptureStatus
                : CaptureVisibility.Status(show, WindowsHideAnswer.NotAsked);
            ApplyRecordingTo(FindName, show, status, L);
        }

        /// <summary>Fills the recordings switch and its line (static, so the render tests run the app's code on the loose XAML).</summary>
        internal static void ApplyRecordingTo(Func<string, object> find, bool show, CaptureStatus status, Func<string, string, string> L)
        {
            string line = RecordingStatusLine(status, L);
            if (find("RecordingStatusText") is TextBlock text) text.Text = line;
            if (find("ShowInRecordingsToggle") is CheckBox toggle)
            {
                toggle.IsChecked = show;
                System.Windows.Automation.AutomationProperties.SetHelpText(toggle, line);
            }
        }

        /// <summary>The sentence under the recordings switch for a status; a neutral one while nothing is known yet.</summary>
        internal static string RecordingStatusLine(CaptureStatus status, Func<string, string, string> L) =>
            CaptureVisibility.StatusKey(status) is string key
                ? L(key, CaptureVisibility.StatusFallback(status))
                : L("look_Recording_Status_Unknown", "When this is off, Kaption asks Windows to keep the subtitles out of screen capture.");

        /// <summary>Applies at once: the overlay stops (or starts) asking Windows to hide it, no restart.</summary>
        private void ShowInRecordingsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingLook || !IsInitialized) return;
            bool show = ShowInRecordingsToggle.IsChecked == true;
            Config.Set(CaptureVisibility.ShowInRecordingsKey, show);
            if (System.Windows.Application.Current?.MainWindow is MainWindow overlay) overlay.ApplyCaptureVisibility();
            _syncingLook = true;
            try { SyncRecordingCard(); }
            finally { _syncingLook = false; }
        }

        private void KaptionMarkToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingLook || !IsInitialized) return;
            Config.Set(OverlayStyle.MarkKey, KaptionMarkToggle.IsChecked == true);
            OverlayLookChanged();
        }

        /// <summary>
        /// After any change on the page: the running overlay, the previews and the looks follow; the sliders and number
        /// boxes too unless the change came from them (<paramref name="syncControls"/> false while a slider moves).
        /// </summary>
        private void OverlayLookChanged(bool syncControls = true)
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow overlay) overlay.ApplyOverlayStyle();
            if (syncControls) SyncLookControls(OverlayStyle.FromConfig());
            SyncLookPage();
        }

        /// <summary>Sliders and number boxes show the saved values (no handler writes while they move).</summary>
        private void SyncLookControls(OverlayStyle look)
        {
            if (FontSizeSlider == null) return;
            bool was = _suppressSliderUpdate;
            _suppressSliderUpdate = true;
            try
            {
                FontSizeSlider.Value = Math.Clamp(look.Size, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
                FontSizeTextBox.Text = look.Size.ToString();
                int pad = look.PadVertical ?? DefaultPadVertical;
                PadVerticalSlider.Value = Math.Clamp(pad, PadVerticalSlider.Minimum, PadVerticalSlider.Maximum);
                PadTextBox.Text = pad.ToString();
                PadHorizontalSlider.Value = Math.Clamp(look.PadHorizontal, PadHorizontalSlider.Minimum, PadHorizontalSlider.Maximum);
                PadHorizontalTextBox.Text = look.PadHorizontal.ToString();
                int maxHeight = Config.Get("MaxOverlayHeight", SubtitleOverlayDefaults.MaxOverlayHeight);
                MaxHeightSlider.Value = maxHeight > 0 ? Math.Clamp(maxHeight, MaxHeightSlider.Minimum, MaxHeightSlider.Maximum) : MaxHeightSlider.Minimum;
                MaxHeightTextBox.Text = maxHeight > 0 ? maxHeight.ToString() : "Auto";
                int maxWidth = Config.Get("MaxOverlayWidth", SubtitleOverlayDefaults.MaxOverlayWidth);
                MaxWidthSlider.Value = maxWidth > 0 ? Math.Clamp(maxWidth, MaxWidthSlider.Minimum, MaxWidthSlider.Maximum) : MaxWidthSlider.Minimum;
                MaxWidthTextBox.Text = maxWidth > 0 ? maxWidth.ToString() : "Auto";
                _syncingLook = true;
                foreach (ComboBoxItem item in FontFamilyComboBox.Items)
                    if (string.Equals(item.Tag as string, look.FontFamily, StringComparison.OrdinalIgnoreCase)) { FontFamilyComboBox.SelectedItem = item; break; }
            }
            finally
            {
                _syncingLook = false;
                _suppressSliderUpdate = was;
            }
        }
    }
}
