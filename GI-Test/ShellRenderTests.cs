using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Core.Logging;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Border = System.Windows.Controls.Border;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The Settings shell with realistic data, filled through the same static code the window runs (state card,
    /// state chip, region preview). Writes review PNGs to %TEMP%\kaption-render\shell-*.png and the screenshots the
    /// website guides use to %TEMP%\kaption-w15\guides\ (1280×800, 100 %).
    /// </summary>
    [TestClass]
    public class ShellRenderTests
    {
        public static string GuidesDir => Path.Combine(Path.GetTempPath(), "kaption-w15", "guides");
        private const double GuideWidth = 1280, GuideHeight = 800;

        public enum Scene { Stopped, Translating, WaitingForGame, Downloading, Updated25, OnHold }

        [TestMethod]
        public void RendersTheStartPageInEveryState()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                foreach (Scene scene in Enum.GetValues(typeof(Scene)))
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    Fill(host, window, scene, lang);
                    Select(window, SettingsNavigation.Home);
                    Layout(host, GuideWidth, GuideHeight);
                    string name = $"start-{SceneSlug(scene)}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                    written.Add(SaveTo(GuidesDir, Snapshot(host), name));
                    written.Add(Save(Snapshot(host), "shell-" + name));
                    written.Add(SaveReview(Snapshot(host), "start/" + name));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheStartHero_ForEveryGame()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (var (game, name, accentKey) in new[] { ("Genshin", "Genshin Impact", "GameGenshinBrush"),
                             ("StarRail", "Honkai: Star Rail", "GameStarRailBrush"), ("ZZZ", "Zenless Zone Zero", "GameZzzBrush") })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, palette);
                    Fill(host, window, Scene.Stopped, "pl-PL");
                    ((ImageBrush)window.FindName("StateCardArt")).ImageSource = Art(game);
                    ((TextBlock)window.FindName("StateCardSpeaker")).Text = name;
                    var accent = host.TryFindResource(accentKey) as Brush;
                    ((TextBlock)window.FindName("StateCardSpeaker")).Foreground = accent;
                    ((Border)window.FindName("StateCardAccent")).Background = accent;
                    SettingsWindow.ApplyAtmosphereTo(window.FindName, game, k => host.TryFindResource(k));
                    Assert.IsNotNull(((Border)window.FindName("StateCardGlow")).Background, $"{game} has its glow");
                    NavRail.UseGameAccent((TabControl)window.FindName("NavTabs"), game);
                    Select(window, SettingsNavigation.Home);
                    Layout(host, GuideWidth, GuideHeight);
                    written.Add(SaveReview(Snapshot(host), $"start/hero-{game.ToLowerInvariant()}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// The hero's text against the game's art as drawn: the band is rendered with the text hidden, and the brightest
        /// 5 % of the backdrop behind each text decides its contrast (WCAG ratio). Every text on the band keeps 4.5:1, the
        /// state line included, on every game's art, with and without the detail line.
        /// </summary>
        [TestMethod]
        public void StartHero_TextKeepsItsContrast_OnEveryGamesArt()
        {
            var report = new List<string>();
            RunSta(() =>
            {
                foreach (Scene scene in new[] { Scene.Stopped, Scene.WaitingForGame })
                foreach (var (game, accentKey) in new[] { ("Genshin", "GameGenshinBrush"), ("StarRail", "GameStarRailBrush"), ("ZZZ", "GameZzzBrush") })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, "Dark");
                    Fill(host, window, scene, "pl-PL");
                    ((ImageBrush)window.FindName("StateCardArt")).ImageSource = Art(game);
                    var speaker = (TextBlock)window.FindName("StateCardSpeaker");
                    speaker.Foreground = host.TryFindResource(accentKey) as Brush;
                    SettingsWindow.ApplyAtmosphereTo(window.FindName, game, k => host.TryFindResource(k));
                    Select(window, SettingsNavigation.Home);

                    var change = (Button)window.FindName("DashChangeTranslationButton");
                    var texts = new List<(string Name, FrameworkElement Box, Color Fg)>
                    {
                        ("speaker", speaker, Fg(speaker.Foreground)),
                        ("state line", (TextBlock)window.FindName("StateCardTitle"), Fg(((TextBlock)window.FindName("StateCardTitle")).Foreground)),
                        ("pair", (TextBlock)window.FindName("DashActiveTranslationText"), Fg(((TextBlock)window.FindName("DashActiveTranslationText")).Foreground)),
                        ("Zmień grę", change, Fg(change.Foreground)),
                    };
                    var detail = (TextBlock)window.FindName("StateCardDetail");
                    if (detail.Visibility == Visibility.Visible) texts.Add(("detail", detail, Fg(detail.Foreground)));

                    Layout(host, GuideWidth, GuideHeight);
                    foreach (var t in texts) t.Box.Opacity = 0;
                    Layout(host, GuideWidth, GuideHeight);
                    var backdrop = Snapshot(host);
                    foreach (var t in texts)
                    {
                        Rect box = InkBox(t.Box, host);
                        double bg = BrightestLuminance(backdrop, box, 0.95);
                        double ratio = ContrastRatio(RelativeLuminance(t.Fg), bg);
                        report.Add($"{game,-8} {scene,-14} {t.Name,-10} {ratio,5:0.0}:1");
                        Assert.IsTrue(ratio >= 4.5, $"{game} {scene}: \"{t.Name}\" has {ratio:0.0}:1 against the art (needs 4.5:1)");
                    }
                }
            });
            foreach (string line in report) Console.WriteLine(line);
        }

        /// <summary>
        /// Start fills the window in every state: when the content is shorter than the page, the tiles stretch and
        /// "Zaawansowane" ends at the bottom margin; when it is taller, the page scrolls and nothing is squeezed.
        /// </summary>
        [TestMethod]
        public void StartPage_FillsTheWindow_InEveryState()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (Scene scene in Enum.GetValues(typeof(Scene)))
                foreach (double height in new[] { GuideHeight, 1000.0, 640.0 })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, palette);
                    Fill(host, window, scene, "pl-PL");
                    Select(window, SettingsNavigation.Home);
                    Layout(host, GuideWidth, height);
                    var scroller = (ScrollViewer)window.FindName("StartScroller");
                    var layout = (Grid)window.FindName("StartLayout");
                    var advanced = (Expander)window.FindName("HomeAdvancedExpander");
                    var packs = (Border)window.FindName("PacksTile");
                    var keys = (Border)window.FindName("HotkeysTile");
                    double bottom = advanced.TransformToAncestor(scroller).Transform(new Point(0, advanced.ActualHeight)).Y;
                    string what = $"{scene} at {height:0} ({palette})";
                    if (scroller.ExtentHeight <= scroller.ViewportHeight + 0.5)
                        Assert.AreEqual(scroller.ViewportHeight - layout.Margin.Bottom, bottom, 1.0, $"{what}: Zaawansowane sits on the bottom edge");
                    else
                        Assert.IsTrue(packs.ActualHeight >= packs.DesiredSize.Height - 0.5, $"{what}: a taller page scrolls instead of squeezing the tiles");
                    Assert.AreEqual(packs.ActualHeight, keys.ActualHeight, 0.5, $"{what}: both tiles share the row's height");
                    if (palette == "Dark" && scene is Scene.Stopped or Scene.WaitingForGame || height == GuideHeight)
                        written.Add(SaveReview(Snapshot(host), $"start/fill-{SceneSlug(scene)}-{height:0}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersEveryRailPageWithData()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    Fill(host, window, Scene.Stopped, lang);
                    foreach (string page in SettingsNavigation.Pages)
                    {
                        if (page == SettingsNavigation.Characters) continue; // W8's page; its own tests render it
                        Select(window, page);
                        Layout(host, GuideWidth, GuideHeight);
                        string name = $"page-{page.ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                        written.Add(SaveTo(GuidesDir, Snapshot(host), name));
                        written.Add(SaveReview(Snapshot(host), "pages/" + name));
                        if (lang == "pl-PL") written.Add(Save(Snapshot(host, 1.25), "shell-" + name.Replace(".png", "@125.png")));
                    }
                    // Advanced mode: every disclosure open (what power users see).
                    foreach (string n in new[] { "HomeAdvancedExpander", "GameAdvancedExpander", "LookAdvancedExpander", "HotkeysAdvancedExpander", "HelpAdvancedExpander" })
                        ((Expander)window.FindName(n)).IsExpanded = true;
                    foreach (string page in new[] { SettingsNavigation.Game, SettingsNavigation.Look, SettingsNavigation.Help })
                    {
                        Select(window, page);
                        Layout(host, GuideWidth, 1900);
                        string advanced = $"advanced-{page.ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                        written.Add(Save(Snapshot(host), "shell-" + advanced));
                        written.Add(SaveReview(Snapshot(host), "pages/" + advanced));
                    }
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheLookPage_InEveryLookGameAndWidth()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    Fill(host, window, Scene.Stopped, lang);
                    Select(window, SettingsNavigation.Look);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    var preview = (SubtitlePreview)window.FindName("LookPreview");
                    foreach (OverlayPreset preset in Enum.GetValues(typeof(OverlayPreset)))
                    {
                        var look = OverlayStyle.Default.WithPreset(preset, preset == OverlayPreset.Cinema
                            ? GI_Subtitles.Services.Rendering.SubtitleCard.ReferenceHeight(OverlayStyle.PresetValues(preset)) + 10 : 0);
                        ApplyLook(window, look, "Genshin", LookFixture, L);
                        Layout(host, GuideWidth, GuideHeight + 200);
                        string name = $"look-{preset.ToString().ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                        written.Add(SaveReview(Snapshot(host), "look/" + name));
                        if (preset == OverlayPreset.Classic)
                        {
                            Layout(host, GuideWidth, GuideHeight);
                            written.Add(SaveTo(GuidesDir, Snapshot(host), $"page-appearance-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                        }
                    }
                    if (lang != "pl-PL") continue;

                    // To scale: a 22 px subtitle on a 2560 px monitor is small in the frame, where the app puts it.
                    ApplyLook(window, OverlayStyle.Default, "Genshin", LookFixture, L);
                    Layout(host, GuideWidth, GuideHeight + 200);
                    double s = preview.FrameScale;
                    Assert.AreEqual(SubtitlePreview.FrameWidth / 2048, s, 0.001, "the frame is the monitor, 640 px wide");
                    Assert.AreEqual(360, preview.Height, 1, "with the monitor's aspect");
                    var card = preview.Card;
                    var rect = LookGeometry.PanelRect(LookFixture, -140, 0, card.DesiredSize,
                        GI_Subtitles.Services.Rendering.SubtitleCard.ReferenceHeight(OverlayStyle.Default) + 10);
                    Assert.AreEqual(rect.Width * s, preview.PanelInFrame.Width, 0.5, "the panel is its real size times the frame's scale");
                    Assert.AreEqual((rect.Y - LookFixture.Frame.Y) * s, preview.PanelInFrame.Y, 0.5, "and where the app places it");

                    foreach (string game in new[] { "StarRail", "ZZZ" })
                    {
                        ApplyLook(window, OverlayStyle.Default, game, LookFixture, L);
                        Layout(host, GuideWidth, GuideHeight + 200);
                        written.Add(SaveReview(Snapshot(host), $"look/look-game-{game.ToLowerInvariant()}-pl-{palette.ToLowerInvariant()}.png"));
                    }
                    // The owner's own setup: dialogue area 341,1062 1842×368 on 2560×1440 at 125 %, size 20, offsets -187 / -318,
                    // and the old "Segoe UI" read as the new default face.
                    var owner = OverlayStyle.Default with { Size = 20, PadVertical = -187, PadHorizontal = -318 };
                    ApplyLook(window, owner, "Genshin", OwnerFixture, L);
                    Layout(host, GuideWidth, GuideHeight + 200);
                    written.Add(SaveReview(Snapshot(host), $"look/look-owner-setup-pl-{palette.ToLowerInvariant()}.png"));
                    // The game open in a 1600×900 window: the frame is that window.
                    ApplyLook(window, OverlayStyle.Default, "Genshin", GameWindowFixture, L);
                    Layout(host, GuideWidth, GuideHeight + 200);
                    written.Add(SaveReview(Snapshot(host), $"look/look-game-window-pl-{palette.ToLowerInvariant()}.png"));
                    Assert.AreEqual(SubtitlePreview.FrameWidth / GameWindowFixture.Frame.Width, preview.FrameScale, 0.001, "the frame is the game window");
                    // An ultrawide monitor: the frame keeps its aspect.
                    ApplyLook(window, OverlayStyle.Default, "Genshin", UltrawideFixture, L);
                    Layout(host, GuideWidth, GuideHeight + 200);
                    written.Add(SaveReview(Snapshot(host), $"look/look-ultrawide-pl-{palette.ToLowerInvariant()}.png"));
                    Assert.AreEqual(Math.Round(640 * 1440.0 / 3440), preview.Height, 1);
                    // The narrow window: the looks in a row under the miniature.
                    ApplyLook(window, OverlayStyle.Default, "Genshin", LookFixture, L);
                    Layout(host, 980, 1300);
                    written.Add(SaveReview(Snapshot(host), $"look/look-narrow-pl-{palette.ToLowerInvariant()}.png"));
                    Assert.IsTrue(((SubtitleTrueSize)window.FindName("LookTrueSize")).Card.ActualWidth > 300, "the 1:1 strip shows the panel at its size");
                    Assert.AreEqual(true, ((System.Windows.Controls.RadioButton)window.FindName("PresetClassic")).IsChecked, "the default look is Klasyczny");
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The page as the app fills it, plus the sliders (code-behind keeps them in step in the app).</summary>
        private static void ApplyLook(Window window, OverlayStyle look, string game, LookScene scene, Func<string, string, string> L)
        {
            SettingsWindow.ApplyLookTo(window.FindName, look, game, scene, -140, 900, Art(game), L);
            ((Slider)window.FindName("FontSizeSlider")).Value = look.Size;
            ((Slider)window.FindName("PadVerticalSlider")).Value = look.PadVertical ?? -140;
            ((Slider)window.FindName("PadHorizontalSlider")).Value = look.PadHorizontal;
        }

        /// <summary>A 2560×1440 monitor at 125 % with a centred Genshin dialogue area, in DIPs.</summary>
        internal static readonly LookScene LookFixture = new(new Rect(0, 0, 2048, 1152), new Rect(0, 0, 2048, 1152),
            new Rect(246, 922, 1556, 150), "Monitor 1 · 2560×1440", false);
        internal static readonly LookScene OwnerFixture = new(new Rect(0, 0, 2048, 1152), new Rect(0, 0, 2048, 1152),
            new Rect(341 / 1.25, 1062 / 1.25, 1842 / 1.25, 368 / 1.25), "Monitor 2 · 2560×1440", false);
        internal static readonly LookScene GameWindowFixture = new(new Rect(384, 216, 1280, 720), new Rect(0, 0, 2048, 1152),
            new Rect(512, 744, 1024, 112), "Okno gry · 1600×900", true);
        internal static readonly LookScene UltrawideFixture = new(new Rect(0, 0, 3440, 1440), new Rect(0, 0, 3440, 1440),
            new Rect(860, 1150, 1720, 190), "Monitor 1 · 3440×1440", false);

        internal static ImageSource Art(string game)
        {
            string path = Path.Combine(RepoRoot(), "GI-Subtitles", "Assets", "Ambience", AmbienceArt.ForGame(game) + ".jpg");
            return File.Exists(path) ? AmbienceArt.LoadFile(path) : null;
        }

        [TestMethod]
        public void RendersTheDialogsWithContent()
        {
            var written = new List<string>();
            string art = Path.Combine(RepoRoot(), "GI-Subtitles", "Assets", "Ambience", AmbienceArt.Welcome + ".jpg");
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                {
                    foreach (string lang in new[] { "pl-PL", "en-US" })
                    foreach (var (view, w, h) in new[] { ("Views/LoginWindow.xaml", 480.0, 560.0), ("Views/EulaAcceptanceWindow.xaml", 560.0, 580.0) })
                    {
                        var host = LoadLooseWindow(view, lang, out var window, palette);
                        if (File.Exists(art)) ((Image)window.FindName("Backdrop")).Source = AmbienceArt.LoadFile(art);
                        if (view.Contains("Eula"))
                        {
                            // The constructor's inline texts, as the app sets them.
                            EulaAcceptanceWindow.ApplyInlineText(window.FindName, (k, f) => host.TryFindResource(k) as string ?? f);
                            string consent = string.Concat(new[] { "RunLegalPrefix", "RunLegalAction", "RunLegalSuffix" }
                                .Select(n => ((System.Windows.Documents.Run)window.FindName(n)).Text));
                            StringAssert.StartsWith(consent, lang == "pl-PL" ? "Klikając Akceptuję i kontynuuj, akceptujesz" : "By clicking Accept & continue, you agree",
                                "the consent line is one sentence in the window's language");
                        }
                        Layout(host, w, h);
                        string name = Path.GetFileNameWithoutExtension(view);
                        string tag = lang.Substring(0, 2);
                        written.Add(Save(Snapshot(host), $"shell-{name}-{tag}-{palette.ToLowerInvariant()}.png"));
                        if (lang == "pl-PL") written.Add(Save(Snapshot(host, 1.25), $"shell-{name}-{tag}-{palette.ToLowerInvariant()}@125.png"));
                        written.Add(SaveReview(Snapshot(host), $"dialogs/{name}-{tag}-{palette.ToLowerInvariant()}.png"));
                    }

                    // A real gate message: the game-not-found confirm, Polish copy from the dictionaries.
                    var dlgHost = LoadLooseWindow("Views/ModernDialog.xaml", "pl-PL", out var dlg, palette);
                    string L(string k) => dlgHost.TryFindResource(k) as string ?? k;
                    ((TextBlock)dlg.FindName("TitleText")).Text = L("Dialog_GameNotFound_Title");
                    ((TextBlock)dlg.FindName("BodyText")).Text = L("Dialog_GameNotFound_Body");
                    var details = (TextBlock)dlg.FindName("DetailsText");
                    details.Text = L("Dialog_GameNotFound_Details");
                    details.Visibility = Visibility.Visible;
                    ((Button)dlg.FindName("PrimaryButton")).Content = L("Dialog_GameNotFound_Primary");
                    var secondary = (Button)dlg.FindName("SecondaryButton");
                    secondary.Content = L("Dialog_GameNotFound_Secondary");
                    secondary.Visibility = Visibility.Visible;
                    ((Border)dlg.FindName("AccentStrip")).SetResourceReference(Border.BackgroundProperty, "WarnBrush");
                    ((Border)dlg.FindName("IconBubble")).SetResourceReference(Border.BackgroundProperty, "WarnLightBrush");
                    var icon = (System.Windows.Shapes.Path)dlg.FindName("IconPath");
                    icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "WarnBrush");
                    icon.Data = Geometry.Parse("M12 2L1 21h22L12 2zm1 14h-2v-2h2v2zm0-4h-2V8h2v4z");
                    dlgHost.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3B));
                    Layout(dlgHost, 492);
                    written.Add(Save(Snapshot(dlgHost), $"shell-ModernDialog-pl-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// "Karta buildu" as a player meets it, in both palettes and languages: the tile in Start's Skróty, the same tile
        /// with the quiet line after a read that found no UID, Start › Zaawansowane with a remembered UID and "Zapomnij UID",
        /// and the Characters tab with the header button and its line. Review renders under build-card/.
        /// </summary>
        [TestMethod]
        public void RendersTheBuildCardEntries()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    string tag = $"{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}";
                    var culture = new CultureInfo(lang);
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
                    Fill(host, window, Scene.Stopped, lang);
                    Select(window, SettingsNavigation.Home);
                    var tile = (Button)window.FindName("BuildCardTile");
                    var note = (TextBlock)window.FindName("BuildCardNote");
                    Layout(host, GuideWidth, GuideHeight);
                    Assert.IsTrue(tile.ActualHeight >= 44, $"the tile is a 44 px target ({tile.ActualHeight:0})");
                    Assert.AreEqual(Visibility.Collapsed, note.Visibility, "no line before a click");
                    var keys = (Border)window.FindName("HotkeysTile");
                    Assert.IsTrue(tile.TransformToAncestor(keys).Transform(new Point(0, 0)).Y > 0, "inside the Skróty tile");
                    written.Add(SaveReview(Snapshot(host), $"build-card/start-{tag}.png"));

                    // No UID on the game screen: one quiet line under the tile, no dialog.
                    note.Text = L("card_Uid_NotRead", "");
                    note.Visibility = Visibility.Visible;
                    Layout(host, GuideWidth, GuideHeight);
                    written.Add(SaveReview(Snapshot(host), $"build-card/start-not-read-{tag}.png"));

                    // Start › Zaawansowane with a UID kept for Genshin and the small action that forgets it, filled the way the
                    // app fills it on expanding (the technical details, then the build card's row). The page is laid out at its
                    // final size first: a resize in the same pass as the expansion leaves this harness's grid row stale.
                    Layout(host, GuideWidth, 1240);
                    var advanced = (Expander)window.FindName("HomeAdvancedExpander");
                    advanced.IsExpanded = true;
                    ((TextBlock)window.FindName("HomeAdvancedDetails")).Text =
                        "Reader: DmlExecutionProvider · PP-OCRv6 Small · GPU on · NVIDIA GeForce RTX 5070 Ti\n" +
                        "Index: 536,737 entries · dictionary 536,737\nPack genshin/pl: 7.1.0-r8 (fv_01a19a16439b)";
                    SettingsWindow.ApplyBuildCardMemoryTo(window.FindName, new[] { ("Genshin", "735366201") }, L, culture);
                    Layout(host, GuideWidth, 1240);
                    var forget = (Button)window.FindName("BuildCardForgetButton");
                    Assert.IsTrue(forget.IsEnabled, "something to forget");
                    Assert.IsTrue(forget.ActualHeight >= 36 && advanced.ActualHeight > 100, $"the row shows ({advanced.ActualHeight:0} px)");
                    StringAssert.Contains(((TextBlock)window.FindName("BuildCardMemoryText")).Text, "Genshin Impact 735366201");
                    written.Add(SaveReview(Snapshot(host), $"build-card/start-advanced-{tag}.png"));

                    // The Characters tab: the header's button next to the kaption.one one, and its line.
                    var page = CharactersTabWithBuildCard(palette, lang, out var pageHost);
                    Layout(pageHost, GuideWidth - 120, GuideHeight - 120);
                    var overview = (ContentControl)page.FindName("OverviewHost");
                    var view = (CharacterOverviewView)overview.Content;
                    var button = (Button)view.FindName("BuildCardButton");
                    var site = (Button)view.FindName("SitePageButton");
                    Assert.AreEqual(Visibility.Visible, button.Visibility);
                    Assert.AreEqual(site.ActualHeight, button.ActualHeight, 0.5, "the two header buttons share a height");
                    written.Add(SaveReview(Snapshot(pageHost), $"build-card/characters-{tag}.png"));
                    view.ShowBuildCardNote(L("card_Uid_NotRead", ""));
                    Layout(pageHost, GuideWidth - 120, GuideHeight - 120);
                    written.Add(SaveReview(Snapshot(pageHost), $"build-card/characters-not-read-{tag}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The Characters tab as Settings hosts it, on a two-character fixture, with a build card that does nothing.</summary>
        private static CharactersPage CharactersTabWithBuildCard(string palette, string lang, out Border host)
        {
            var catalog = GI_Subtitles.Services.Characters.CharacterCatalog.Load(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CharacterCatalogTests.MinimalJson)), "starrail");
            var page = new CharactersPage
            {
                GameSource = () => "StarRail", PackSource = () => null, PackReady = () => true, FetchImages = false,
                BuildCardSource = _ => System.Threading.Tasks.Task.CompletedTask,
            };
            host = new Border { Padding = new Thickness(20, 16, 20, 12), UseLayoutRounding = true };
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{palette}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            host.Child = page;
            page.UsePalette(palette == "Dark");
            var load = page.LoadAsync("StarRail", _ => System.Threading.Tasks.Task.FromResult(catalog));
            CharacterOverviewRenderTests.Pump(() => load.IsCompleted);
            var overview = (ContentControl)page.FindName("OverviewHost");
            CharacterOverviewRenderTests.Pump(() => overview.Content is CharacterOverviewView);
            return page;
        }

        // ── fixture ─────────────────────────────────────────────────────────

        internal static string SceneSlug(Scene s) => s switch
        {
            Scene.Translating => "tlumacze",
            Scene.WaitingForGame => "czekam-na-gre",
            Scene.Downloading => "pobieram",
            Scene.Updated25 => "nowosc-25",
            Scene.OnHold => "wstrzymany",
            _ => "zatrzymano",
        };

        private static void Select(Window window, string page)
        {
            var tabs = (TabControl)window.FindName("NavTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string)t.Tag == page);
        }

        /// <summary>Fills the loose Settings XAML like the running app would on a Genshin PC.</summary>
        internal static void Fill(Border host, Window window, Scene scene, string lang)
        {
            string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
            var culture = new CultureInfo(lang);
            object Find(string n) => window.FindName(n);
            string repo = RepoRoot();

            ((TitleBar)Find("WindowTitleBar")).Subtitle = "v2.5.1";
            ((Button)Find("VersionChip")).Content = "v2.5.1";

            // State card: Genshin, its hub art, the scene's state.
            var art = Path.Combine(repo, "GI-Subtitles", "Assets", "Ambience", "genshin.jpg");
            if (File.Exists(art)) ((ImageBrush)Find("StateCardArt")).ImageSource = AmbienceArt.LoadFile(art);
            ((TextBlock)Find("StateCardSpeaker")).Text = "Genshin Impact";
            SettingsWindow.ApplyAtmosphereTo(Find, "Genshin", k => host.TryFindResource(k));
            NavRail.UseGameAccent((TabControl)Find("NavTabs"), "Genshin"); // the rail's indicator in the game's colour, as the app does
            ((TextBlock)Find("DashActiveTranslationText")).Text = "English → Polski";
            var inputs = scene switch
            {
                Scene.Translating => new StartCardInputs { Translating = true, LinesThisSession = 37, Reader = ReaderState.Ready, PackOnDisk = true },
                Scene.WaitingForGame or Scene.Updated25 => new StartCardInputs { WaitingForGame = true, Reader = ReaderState.Ready, PackOnDisk = true, GameName = "Genshin Impact" },
                // A 2.4 upgrader with auto-start on who never saw the display-mode tip.
                Scene.OnHold => new StartCardInputs { WaitingForGame = true, Reader = ReaderState.Ready, PackOnDisk = true, GameName = "Genshin Impact",
                    AutoStartBlockedBy = GI_Subtitles.Services.Automation.StartGate.DisplayModeTip },
                Scene.Downloading => new StartCardInputs { Downloading = true, DownloadFraction = 0.42, DownloadBytesTotal = 100_663_296, Reader = ReaderState.Loading },
                _ => new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true },
            };
            var state = StartCardModel.Compute(inputs);
            SettingsWindow.ApplyStartCardTo(Find, state, inputs, L, culture);
            SettingsWindow.ApplyStateChipTo(Find, state, L);
            if (scene == Scene.Downloading) ((TextBlock)Find("DashEngineStatus")).Text = L("shell_Chip_ReaderStarting", "");
            ((TextBlock)Find("DashDictStatus")).Text = scene == Scene.Downloading
                ? L("shell_Chip_DictLoading", "")
                : string.Format(culture, L("shell_Chip_Dict_Format", "{0:N0}"), 536_737);

            // Packs, reader, shortcuts: values from a real install (2026-11-02).
            Brush B(string key) => host.TryFindResource(key) as Brush;
            SettingsWindow.PackCardRow Row(string name, string accent, string installed, string latest, bool active, bool downloading = false)
            {
                var (text, brush, isDownload, sub) = SettingsWindow.PackRowStatus(PackStatus.Compare(installed, latest), downloading, active,
                    installed, latest, L, culture);
                return new SettingsWindow.PackCardRow { Name = name, Version = sub, Status = text, StatusBrush = B(brush), Accent = B(accent),
                    IsDownload = isDownload, Tooltip = isDownload ? L("shell_Packs_Download_Tip", "") : null };
            }
            ((ItemsControl)Find("PacksCardList")).ItemsSource = new[]
            {
                Row("Genshin Impact", "GameGenshinBrush", "7.1.0-r8", "7.1.0-r8", active: true),
                Row("Honkai: Star Rail", "GameStarRailBrush", "4.6.0-r3", "4.6.0-r5", active: false),
                Row("Zenless Zone Zero", "GameZzzBrush", scene == Scene.Downloading ? "3.3.0-r2" : null, "3.3.0-r3", active: false, downloading: scene == Scene.Downloading),
            };
            ((UIElement)Find("PacksCardList")).Visibility = Visibility.Visible;
            ((UIElement)Find("PacksCardSkeleton")).Visibility = Visibility.Collapsed;
            // The reader is a status-bar chip now: "GPU · PP-OCRv6", the card's product name in its tooltip.
            if (scene != Scene.Downloading)
            {
                ((TextBlock)Find("DashEngineStatus")).Text = EngineLabels.Chip(true, "PP-OCRv6 Small", L);
                ((FrameworkElement)Find("EngineChip")).ToolTip = string.Format(culture, L("shell_Chip_Gpu_Tip", "{0}"),
                    EngineLabels.ProductName("NVIDIA GeForce RTX 5070 Ti (vendor 0x10DE, device 0x2C05, 15997 MB dedicated)"));
            }
            var adapterLine = (TextBlock)Find("OcrGpuAdapterText");
            adapterLine.Text = string.Format(culture, L("start_Diag_Adapter_Format", "{0}"),
                "NVIDIA GeForce RTX 5070 Ti (vendor 0x10DE, device 0x2C05, 15997 MB dedicated)");
            adapterLine.Visibility = Visibility.Visible;
            ((ItemsControl)Find("HotkeysCardList")).ItemsSource = new[]
            {
                new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9000", ""), Keys = new[] { "Ctrl", "Shift", "S" } },
                new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9005", ""), Keys = new[] { "Ctrl", "Q" } },
                new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9004", ""), Keys = new[] { "Ctrl", "1" } },
                new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9002", ""), Keys = new[] { "Ctrl", "Shift", "H" } },
            };
            // A returning user's first start of 2.5: what changed, with the switch.
            ((UIElement)Find("Intro25Card")).Visibility = scene == Scene.Updated25 ? Visibility.Visible : Visibility.Collapsed;
            Assert.IsTrue(SettingsWindow.ApplyWhatsNewTo(Find, LatestNote, seenReleaseId: null, now: LatestNote.PublishedAt.AddDays(1), L, culture, B),
                    "a release note from yesterday shows the card");

            // Game page: pills, packs list, the dialogue area on a real Genshin frame.
            ((Button)Find("GamePillGenshin")).Style = host.TryFindResource("ModernButton") as Style ?? ((Button)Find("GamePillGenshin")).Style;
            ((Button)Find("InputPillEN")).Style = host.TryFindResource("ModernButton") as Style ?? ((Button)Find("InputPillEN")).Style;
            TranslationPackInfo.Lookup = key => host.TryFindResource(key);
            var packs = new List<TranslationPackInfo>
            {
                new TranslationPackInfo { Game = "genshin", Language = "pl", GameDisplayName = "Genshin Impact", LanguageDisplayName = "Polski",
                    Source = TranslationPackSource.PaidCached, LocalPath = @"C:\Users\Gracz\AppData\Roaming\Kaption\Genshin\TextMapPL.gisub",
                    LocalSize = 92_901_376, LocalModifiedAt = DateTime.Now.AddHours(-3), LocalVersion = "7.1.0-r8", RemoteVersion = "7.1.0-r8",
                    DirectionLabel = "English → Polski", IsActiveTarget = true },
                new TranslationPackInfo { Game = "starrail", Language = "pl", GameDisplayName = "Honkai: Star Rail", LanguageDisplayName = "Polski",
                    Source = TranslationPackSource.RemoteAvailable, RemoteSize = 64_487_424, RemoteUnlocked = true, RemoteVersion = "4.6.0-r5",
                    DirectionLabel = "English → Polski" },
            };
            var view = new ListCollectionView(packs);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TranslationPackInfo.GameDisplayName)));
            ((ListView)Find("TranslationsList")).ItemsSource = view;
            ((UIElement)Find("TranslationsLoading")).Visibility = Visibility.Collapsed;
            string frame = new[]
            {
                Path.Combine(repo, "GI-Test", "bin", "Debug", "Images", "en-pl-npc-dialog.jpg"),
                @"F:\Pobrane\Nowy folder (3)\kaption-promo-landing\GI-Test\bin\Debug\Images\en-pl-npc-dialog.jpg",
            }.FirstOrDefault(File.Exists);
            if (frame != null)
            {
                var shot = new BitmapImage(new Uri(frame));
                var region = new System.Drawing.Rectangle(160, 955, 1650, 255);
                var area = SettingsWindow.PreviewArea(region, new System.Drawing.Rectangle(0, 0, shot.PixelWidth, shot.PixelHeight));
                var crop = new CroppedBitmap(shot, new Int32Rect(area.X, area.Y, area.Width, area.Height));
                ((Image)Find("RegionPreviewImage")).Source = SettingsWindow.ComposeRegionPreview(crop,
                    new Rect(region.X - area.X, region.Y - area.Y, region.Width, region.Height), B("GameGenshinBrush"));
                ((UIElement)Find("RegionPreviewEmpty")).Visibility = Visibility.Collapsed;
            }
            ((TextBlock)Find("RegionMonitorText")).Text = string.Format(culture, L("shell_Region_Monitor_Format", ""), 1, 2560, 1440);
            ((TextBlock)Find("DashRegionInfo")).Text = string.Format(culture, L("shell_Region_Set_Format", ""), 1650, 255, 160, 955);

            // Wygląd napisów: the saved look (defaults here), the game's art and its dialogue area.
            SettingsWindow.ApplyLookTo(Find, OverlayStyle.Default, "Genshin", LookFixture, -140, 900, Art("Genshin"), L);

            // Shortcuts page: the real defaults and descriptions.
            var hotkeys = new List<HotkeyViewModel>();
            foreach (var (id, ctrl, shift, key, advanced) in new[]
            {
                (9000, true, true, 'S', false), (9001, true, true, 'R', false), (9003, true, true, 'D', false),
                (9005, true, false, 'Q', false), (9006, true, true, 'Q', false), (9002, true, true, 'H', true), (9004, true, false, '1', true),
            })
                hotkeys.Add(new HotkeyViewModel { Id = id, IsCtrl = ctrl, IsShift = shift, SelectedKey = key, IsAdvanced = advanced,
                    Description = L($"Hotkey_{id}_Description", id.ToString()), InfoText = L($"Hotkey_{id}_Info", "") });
            ((ListView)Find("hotkeyListView")).ItemsSource = hotkeys.Where(h => !h.IsAdvanced).ToList();
            ((ListView)Find("hotkeyListViewAdvanced")).ItemsSource = hotkeys.Where(h => h.IsAdvanced).ToList();
            ((UIElement)Find("HotkeysSkeleton")).Visibility = Visibility.Collapsed;

            // Help page: a few log lines from a real session.
            var t0 = new DateTime(2026, 11, 2, 21, 14, 3, 120);
            ((ListView)Find("LogListView")).ItemsSource = new[]
            {
                new LogEntry(t0, "INFO", "OCR engine loaded successfully — model: PP-OCRv6 Small, provider: DmlExecutionProvider, GPU active: True"),
                new LogEntry(t0.AddSeconds(1.4), "INFO", "Matcher loaded from cache: 536,737 entries in 1,182 ms"),
                new LogEntry(t0.AddSeconds(9.9), "INFO", "Pacing: OcrIntervalMs=100 (profile), StabilityWindowFrames=5 (profile)"),
                new LogEntry(t0.AddSeconds(31.2), "WARN", "Hotkey Ctrl+Shift+D (id 9003) could not be registered; another application may own it."),
                new LogEntry(t0.AddSeconds(64.0), "INFO", "Translated: \"Commissions? ...Oh, I know\" → \"Zlecenia? ...Och, wiem\""),
            };
        }

        /// <summary>The newest note on api.kaption.one when the screenshots were made (2026-11-02, 07:07 UTC).</summary>
        internal static readonly ReleaseNote LatestNote = new()
        {
            ReleaseId = "rel_01a19a16540bed71e5b577", Slug = "zzz-3-3-r4", Game = "zzz", GameVersion = "3.3", Kind = "revision", NewLines = 10,
            PublishedAt = DateTimeOffset.FromUnixTimeSeconds(1793601112),
        };

        private static string SaveTo(string dir, BitmapSource bitmap, string name)
        {
            AssertNotBlank(bitmap, name);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

    }
}
