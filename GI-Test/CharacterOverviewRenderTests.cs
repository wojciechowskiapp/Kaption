using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Renders the character page to PNG in its three hosts: the Ctrl+Q popup (dark, 420 px), the Characters tab
    /// (light, at 820×760 and 1280×800 windows) and the tab's loading / empty / error states. States per sample:
    /// full data (AI notes, teams, ambience, images), no AI, no images. Output: %TEMP%\kaption-render\.
    /// Images come from the app's own cache; set KAPTION_RENDER_FETCH=1 to download missing ones first.
    /// Inconclusive when tools/out holds no character data, except the goal-table render, which has its own fixture.
    /// </summary>
    [TestClass]
    public class CharacterOverviewRenderTests
    {
        private const double PopupWidth = 420;
        private const double DetailWidth = 600;

        private sealed class Sample
        {
            public string File;      // output name
            public string Game;      // wire id
            public string Character; // English name
            public bool InjectAi;
            public bool NoImages;
            public bool NoSplash;
            public bool Expand;
        }

        private static readonly Sample[] Samples =
        {
            new Sample { File = "chiori-full", Game = "genshin", Character = "Chiori", InjectAi = true, Expand = true },
            new Sample { File = "chiori-nosplash", Game = "genshin", Character = "Chiori", InjectAi = true, NoSplash = true },
            new Sample { File = "bennett", Game = "genshin", Character = "Bennett" },
            new Sample { File = "skirk-noai", Game = "genshin", Character = "Skirk" },
            new Sample { File = "kafka", Game = "starrail", Character = "Kafka" },
            new Sample { File = "ellen", Game = "zzz", Character = "Ellen" },
            new Sample { File = "chiori-noimages", Game = "genshin", Character = "Chiori", InjectAi = true, NoImages = true },
        };

        private static bool Fetch => Environment.GetEnvironmentVariable("KAPTION_RENDER_FETCH") == "1";

        [TestMethod]
        public void RendersEverySampleInBothHosts()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (var sample in Samples)
                {
                    var model = BuildModel(root, sample);
                    if (model == null) continue;
                    foreach (bool dark in new[] { true, false })
                    {
                        string name = $"char-{sample.File}-{(dark ? "popup" : "page")}-pl.png";
                        written.Add(Save(RenderView(model, dark, "pl-PL", expand: false), name));
                        if (sample.Expand && !dark)
                            written.Add(Save(RenderView(model, dark, "pl-PL", expand: true), name.Replace(".png", "-expanded.png")));
                    }
                    if (sample.File == "chiori-full")
                        written.Add(Save(RenderView(model, false, "en-US", expand: false), $"char-{sample.File}-page-en.png"));
                }
            });
            if (written.Count == 0) Assert.Inconclusive("No tools/out/<game>/characters-*.json found.");
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheCharactersTab()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var catalog = LoadCatalog(root, "genshin", injectAiFor: "Chiori");
            if (catalog == null) Assert.Inconclusive("No tools/out/genshin/characters-*.json found.");
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (var (w, h) in new[] { (1264.0, 761.0), (804.0, 721.0) })
                {
                    var page = Page(catalog, "Chiori", out var host);
                    Layout(host, w, h);
                    var list = (ListBox)page.FindName("CharacterList");
                    list.ScrollIntoView(list.SelectedItem); // needs a laid-out list
                    Layout(host, w, h);
                    written.Add(Save(Snapshot(host), $"tab-characters-{w + 16:0}x{h + 39:0}-pl.png"));
                }
                {
                    // The same page in a dark-themed host: it picks the dark character palette by itself.
                    var page = Page(catalog, "Chiori", out var host, dark: true);
                    Layout(host, 1264, 761);
                    var list = (ListBox)page.FindName("CharacterList");
                    list.ScrollIntoView(list.SelectedItem);
                    Layout(host, 1264, 761);
                    written.Add(Save(Snapshot(host), "tab-characters-1280x800-dark-pl.png"));
                }
                foreach (bool dark in new[] { false, true })
                {
                    // Settings, Characters: the goal table and the team rank badges scrolled into view, in both palettes.
                    var page = Page(catalog, "Chiori", out var host, dark);
                    Layout(host, 1264, 761);
                    var view = (CharacterOverviewView)((ContentControl)page.FindName("OverviewHost")).Content;
                    var scroller = (ScrollViewer)view.FindName("Scroller");
                    view.ScrollTo(scroller.ScrollableHeight); // lays the lower sections out
                    Layout(host, 1264, 761);
                    var table = (FrameworkElement)view.FindName("StatGoalsTable");
                    double top = table.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
                    view.ScrollTo(Math.Max(0, scroller.VerticalOffset + top - 96));
                    Layout(host, 1264, 761);
                    written.Add(Save(Snapshot(host), $"tab-characters-1280x800-goals{(dark ? "-dark" : "")}-pl.png"));
                    Assert.IsTrue(view.ScrollToSection("Teams"));
                    Layout(host, 1264, 761);
                    written.Add(Save(Snapshot(host), $"tab-characters-1280x800-teams{(dark ? "-dark" : "")}-pl.png"));
                }
                foreach (var state in new[] { CharactersPage.PageState.Loading, CharactersPage.PageState.Empty, CharactersPage.PageState.Error })
                {
                    var page = Page(CharacterCatalog.Empty, null, out var host);
                    page.ShowState(state);
                    Layout(host, 804, 721);
                    written.Add(Save(Snapshot(host), $"tab-characters-{state.ToString().ToLowerInvariant()}-pl.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheQuickTranslatePopup()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var written = new List<string>();
            RunSta(() =>
            {
                var segments = new[]
                {
                    new QuickTranslateSegment("Tamoto", "Tamoto", "Tamoto", true),
                    new QuickTranslateSegment(
                        "Chiori dashes forward and leaves the automaton doll Tamoto behind to keep slashing nearby opponents.",
                        "k1", "Chiori robi doskok i zostawia lalkę Tamoto, która dalej tnie pobliskich przeciwników.", true),
                    new QuickTranslateSegment("Hold to dash further.", "k2", "Przytrzymaj, żeby doskoczyć dalej.", true),
                };
                var popup = new QuickTranslatePopup(segments, QuickTranslateSettings.FromValues(null, 8, 16),
                    new System.Drawing.Rectangle(400, 300, 520, 160));
                var chrome = (FrameworkElement)popup.Content;
                var resources = popup.Resources;
                popup.Content = null;
                popup.Resources = new ResourceDictionary();
                var host = new Border { Child = chrome, UseLayoutRounding = true, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x55)), Padding = new Thickness(16) };
                host.Resources.MergedDictionaries.Add(resources);
                foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
                Layout(host, 520);
                written.Add(Save(Snapshot(host), "popup-translation-pl.png")); // nothing recognised: no strip

                // A skill screen: the character is recognised, the skill title is marked, the strip offers the card.
                var model = BuildModel(root, Samples[0]);
                var catalog = LoadCatalog(root, "genshin", injectAiFor: "Chiori");
                var record = catalog?.Characters.FirstOrDefault(c => c.Name == "Chiori");
                if (model != null && record != null)
                {
                    var skillScreen = new[]
                    {
                        new QuickTranslateSegment("Fluttering Hasode", "k0", "Powiewająca Hasode", true),
                        new QuickTranslateSegment(
                            "Dashes nimbly forward with silken steps. Once this dash ends, Chiori will summon the automaton doll \"Tamoto\" beside her.",
                            "k1", "Zwinnie doskakuje jedwabnymi krokami. Gdy doskok się skończy, Chiori przywołuje obok siebie lalkę automat \"Tamoto\".", true),
                        new QuickTranslateSegment("Hold to dash further.", "k2", "Przytrzymaj, żeby doskoczyć dalej.", true),
                    };
                    var recognised = new QuickTranslatePopup(skillScreen, QuickTranslateSettings.FromValues(null, 8, 16),
                        new System.Drawing.Rectangle(400, 300, 520, 160));
                    var chrome2 = (FrameworkElement)recognised.Content;
                    var resources2 = recognised.Resources;
                    recognised.Content = null;
                    recognised.Resources = new ResourceDictionary();
                    var host2 = new Border { Child = chrome2, UseLayoutRounding = true, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x55)), Padding = new Thickness(16) };
                    host2.Resources.MergedDictionaries.Add(resources2);
                    foreach (var d in Strings("pl-PL")) host2.Resources.MergedDictionaries.Add(d);

                    var context = new QuickTranslateContext(model, record, skillScreen.Select(s => s.Source).ToList());
                    recognised.SetCharacter(new CharacterOverviewView(model), context, () => model.ToPlainText());
                    Layout(host2, 520);
                    written.Add(Save(Snapshot(host2), "popup-recognised-pl.png"));

                    // "Otwórz kartę": the page opens inline, the strip heads it, "Wróć do tłumaczenia" on top.
                    ((Button)recognised.FindName("OpenCardButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    chrome2.MaxHeight = 900;
                    Layout(host2, 452);
                    var page = (CharacterOverviewView)((ContentControl)recognised.FindName("OverviewHost")).Content;
                    var active = ((CharacterOverviewViewModel)page.DataContext).Sections.Single(s => s.IsActive).Key;
                    Console.WriteLine($"expanded: active section {active}, offset {page.VerticalOffset:0}");
                    Assert.AreEqual("Skills", active, "the card opens at the recognised skill");
                    written.Add(Save(Snapshot(host2), "popup-expanded-pl.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// Goal stats and team ranks from a built-in fixture (no tools/out needed): the goal table in the build section and
        /// the rank badges on the team cards and the "Gra też w drużynach" row, in the popup (dark) and on the page (light
        /// and dark), Polish and English. Writes char-goals-*.png (whole page, the table, the teams).
        /// </summary>
        [TestMethod]
        public void RendersTheGoalTableAndRankBadges()
        {
            const string dash = "\u2013", thin = "\u202F";
            var catalog = CharacterStatGoalsTests.Genshin();
            var written = new List<string>();
            var contrast = new List<string>();
            RunSta(() =>
            {
                foreach (var (dark, popup, lang) in new[] { (true, true, "pl-PL"), (false, false, "pl-PL"), (false, false, "en-US"), (true, false, "pl-PL") })
                {
                    var model = CharacterOverviewBuilder.Build(catalog.Find(CharacterStatGoalsTests.Chiori), catalog, null);
                    var (host, view) = GoalHost(model, dark, popup, lang);
                    string name = $"char-goals-{(popup ? "popup" : dark ? "page-dark" : "page")}-{lang.Substring(0, 2)}";
                    bool polish = lang == "pl-PL";

                    var table = (FrameworkElement)view.FindName("StatGoalsTable");
                    Assert.IsTrue(Shown(table), $"{name}: the goal table shows");
                    // Values are drawn in runs (with a spacer before each thousands gap); their accessible name is the text.
                    var texts = Descendants<TextBlock>(table).Where(Shown)
                        .Select(t => System.Windows.Automation.AutomationProperties.GetName(t) is { Length: > 0 } n ? n : t.Text).ToList();
                    string thousands = polish ? "2" + thin + "500" + dash + "3" + thin + "000+" : "2,500" + dash + "3,000+";
                    foreach (string expected in new[] { "CRIT Rate", "CRIT DMG", "DEF", "Energy Recharge", "60" + dash + "70%", "120%+", thousands, "100" + dash + "130%" })
                        CollectionAssert.Contains(texts, expected, $"{name}: {string.Join(" | ", texts)}");
                    string note = Descendants<TextBlock>(table).Where(Shown)
                        .Select(t => System.Windows.Automation.AutomationProperties.GetName(t)).FirstOrDefault(n => n.StartsWith("Fluttering", StringComparison.Ordinal));
                    Assert.AreEqual(polish ? "Fluttering Hasode (E) liczy obrażenia z DEF i ATK." : "Fluttering Hasode (E) scales with DEF and ATK.", note, name);

                    // The thousands gaps: one spacer before each U+202F in Polish, none in English; the runs keep the text.
                    var value = Descendants<TextBlock>(table).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == thousands);
                    Assert.AreEqual(polish ? 2 : 0, value.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count(), name);
                    Assert.AreEqual(thousands, string.Concat(value.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)), name);

                    var teams = (FrameworkElement)view.FindName("Section_Teams");
                    var badges = Descendants<TextBlock>(teams).Where(Shown).Where(t => t.Text.Length == 2 && t.Text[0] == 'C' && char.IsDigit(t.Text[1])).ToList();
                    CollectionAssert.AreEquivalent(new[] { "C0", "C6", "C1" }, badges.Select(b => b.Text).ToList(), $"{name}: Navia C0, Gorou C6, and C1 on the Navia row");

                    var full = Snapshot(host);
                    written.Add(Save(full, name + ".png"));
                    written.Add(Save(Crop(full, table, host), name + "-table.png"));
                    written.Add(Save(Crop(full, teams, host), name + "-teams.png"));

                    var card = Color.FromRgb(0, 0, 0);
                    if (view.TryFindResource("Char_CardBg") is SolidColorBrush bg) card = bg.Color;
                    foreach (var badge in badges.Take(1))
                        contrast.Add($"{name}: badge {Fg(badge.Foreground)} on card {card}, {ContrastRatio(RelativeLuminance(Fg(badge.Foreground)), RelativeLuminance(card)):0.00}:1");
                }
            });
            foreach (string line in contrast) Console.WriteLine(line);
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// Rotations from a built-in fixture (no tools/out): Varka's card with the owner's example and a second rotation, popup
        /// and page, both palettes, PL and EN; Chiori's Geo card with the skill icons the app's cache holds. Writes
        /// char-rotations-*.png and a -team close-up of each; checks "ze Skirk" / "with Skirk".
        /// </summary>
        [TestMethod]
        public void RendersTeamRotations()
        {
            var catalog = CharacterRotationTests.Genshin();
            var cache = CharacterImageCache.ForGame("Genshin");
            var written = new List<string>();
            var icons = new List<string>();
            // A text built from runs (the tag "z" + " " + "Prune", a chip "CA" + " ×2") has no Text of its own: read the runs.
            static string TextOf(TextBlock t) =>
                t.Text.Length > 0 ? t.Text : string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            RunSta(() =>
            {
                foreach (var (id, name, dark, popup, lang) in new[]
                         {
                             (CharacterRotationTests.Varka, "char-rotations-popup-pl", true, true, "pl-PL"),
                             (CharacterRotationTests.Varka, "char-rotations-page-pl", false, false, "pl-PL"),
                             (CharacterRotationTests.Varka, "char-rotations-page-en", false, false, "en-US"),
                             (CharacterRotationTests.Varka, "char-rotations-page-dark-pl", true, false, "pl-PL"),
                             (CharacterRotationTests.Chiori, "char-rotations-icons-popup-pl", true, true, "pl-PL"),
                             (CharacterRotationTests.Chiori, "char-rotations-icons-page-pl", false, false, "pl-PL"),
                         })
                {
                    var model = CharacterOverviewBuilder.Build(catalog.Find(id), catalog, null);
                    CharacterIcons.ApplyCached(model, cache);
                    if (Fetch) Task.Run(() => CharacterIcons.FetchMissingAsync(model, cache)).Wait(TimeSpan.FromSeconds(60));
                    CharacterOverviewViewModel.PrepareVisuals(model);
                    var (host, view) = GoalHost(model, dark, popup, lang);
                    bool polish = lang == "pl-PL";

                    var teams = (FrameworkElement)view.FindName("Section_Teams");
                    var strip = Descendants<ItemsControl>(teams).Single(i => i.Name == "TeamRotations");
                    Assert.IsTrue(Shown(strip), $"{name}: the rotations show under the team's why");
                    var texts = Descendants<TextBlock>(strip).Where(Shown).Select(TextOf).ToList();
                    string all = string.Join(" | ", texts);
                    Assert.AreEqual(2, texts.Count(t => t == (polish ? "Rotacja" : "Rotation")), $"{name}: a heading per rotation: {all}");
                    Assert.AreEqual(5, texts.Count(t => t == ">"), $"{name}: a > between turns (3 + 2): {all}");
                    if (id == CharacterRotationTests.Varka)
                    {
                        CollectionAssert.Contains(texts, polish ? "z Prune" : "with Prune", all);
                        Assert.AreEqual(2, texts.Count(t => t == "* " + CharacterRotationTests.VarkaCombo), $"{name}: the combo footnote: {all}");
                        foreach (string chip in new[] { "CA ×2", "D", "P", "Combo*" })
                            CollectionAssert.Contains(texts, chip, $"{name}: {all}");
                        Assert.AreEqual(2, texts.Count(t => t == "×2"), $"{name}: the badges on Durin's E and Varka's N: {all}");
                    }

                    // Never wider than the card: every turn sits inside the strip, wrapping instead of scrolling.
                    foreach (var turn in Descendants<DockPanel>(strip).Where(Shown))
                    {
                        var box = turn.TransformToAncestor(strip).TransformBounds(new Rect(turn.RenderSize));
                        Assert.IsTrue(box.Right <= strip.ActualWidth + 0.5, $"{name}: a turn ends at {box.Right:0.#} in a {strip.ActualWidth:0.#} px strip");
                    }

                    var discs = ((CharacterOverviewViewModel)view.DataContext).Teams.SelectMany(t => t.Rotations)
                        .SelectMany(r => r.Steps).SelectMany(s => s.Chips).Where(c => c.IsIcon).ToList();
                    icons.Add($"{name}: {discs.Count(c => c.IconPath != null)} of {discs.Count} icon chips drawn from cached skill icons");

                    var full = Snapshot(host);
                    written.Add(Save(full, name + ".png"));
                    written.Add(Save(Crop(full, teams, host), name + "-team.png"));
                }

                // "ze" before a name like Skirk, as the site's plWith; English says "with" either way.
                string skirk = "[ { \"variant\": \"Skirk\", \"steps\": [ { \"member\": \"" + CharacterRotationTests.Durin + "\", \"actions\": [ { \"a\": \"E\" } ] } ] } ]";
                var withSkirk = CharacterRotationTests.Genshin(skirk);
                foreach (var (lang, expected) in new[] { ("pl-PL", "ze Skirk"), ("en-US", "with Skirk") })
                {
                    var model = CharacterOverviewBuilder.Build(withSkirk.Find(CharacterRotationTests.Varka), withSkirk, null);
                    var (_, view) = GoalHost(model, false, false, lang);
                    var strip = Descendants<ItemsControl>((FrameworkElement)view.FindName("Section_Teams")).Single(i => i.Name == "TeamRotations");
                    var tags = Descendants<TextBlock>(strip).Where(Shown).Select(TextOf).ToList();
                    CollectionAssert.Contains(tags, expected, string.Join(" | ", tags));
                }
            });
            foreach (string line in icons) Console.WriteLine(line);
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>
        /// Round 4 flags from a built-in fixture (no tools/out): held skills ("H" on the icon), optional inputs (dashed, a whole
        /// optional turn too), choices (two icons in one pill, "N / CA" as text) and a turn's note, on Varka's card (letters on
        /// the discs) and Chiori's (the cached skill icons); popup and page, both palettes, PL and EN. Writes
        /// char-rotations-flags-*.png with a -team close-up and a -team@2x one.
        /// </summary>
        [TestMethod]
        public void RendersRotationFlags()
        {
            var catalog = CharacterRotationFlagTests.Flags();
            var cache = CharacterImageCache.ForGame("Genshin");
            var written = new List<string>();
            var icons = new List<string>();
            static string TextOf(TextBlock t) =>
                t.Text.Length > 0 ? t.Text : string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            RunSta(() =>
            {
                foreach (var (id, name, dark, popup, lang) in new[]
                         {
                             (CharacterRotationTests.Varka, "char-rotations-flags-popup-pl", true, true, "pl-PL"),
                             (CharacterRotationTests.Varka, "char-rotations-flags-page-pl", false, false, "pl-PL"),
                             (CharacterRotationTests.Varka, "char-rotations-flags-page-en", false, false, "en-US"),
                             (CharacterRotationTests.Varka, "char-rotations-flags-page-dark-pl", true, false, "pl-PL"),
                             (CharacterRotationTests.Chiori, "char-rotations-flags-icons-popup-pl", true, true, "pl-PL"),
                             (CharacterRotationTests.Chiori, "char-rotations-flags-icons-page-pl", false, false, "pl-PL"),
                             (CharacterRotationTests.Chiori, "char-rotations-flags-icons-page-en", false, false, "en-US"),
                         })
                {
                    var model = CharacterOverviewBuilder.Build(catalog.Find(id), catalog, null);
                    CharacterIcons.ApplyCached(model, cache);
                    if (Fetch) Task.Run(() => CharacterIcons.FetchMissingAsync(model, cache)).Wait(TimeSpan.FromSeconds(60));
                    CharacterOverviewViewModel.PrepareVisuals(model);
                    var (host, view) = GoalHost(model, dark, popup, lang);
                    bool polish = lang == "pl-PL", varka = id == CharacterRotationTests.Varka;

                    var teams = (FrameworkElement)view.FindName("Section_Teams");
                    var strip = Descendants<ItemsControl>(teams).Single(i => i.Name == "TeamRotations");
                    Assert.IsTrue(Shown(strip), $"{name}: the rotation shows under the team's why");
                    var texts = Descendants<TextBlock>(strip).Where(Shown).Select(TextOf).ToList();
                    string all = string.Join(" | ", texts);
                    Assert.AreEqual(varka ? 3 : 2, texts.Count(t => t == ">"), $"{name}: a > between turns: {all}");
                    Assert.AreEqual(2, texts.Count(t => t == "H"), $"{name}: the H on each held icon: {all}");
                    Assert.AreEqual(1, texts.Count(t => t == "/"), $"{name}: one pair of icons: {all}");
                    Assert.AreEqual(1, texts.Count(t => t == "×2"), $"{name}: one count badge: {all}");
                    foreach (string expected in varka
                                 ? new[] { "N / CA", "D", "Combo*", "Omit if C6", "Hold the Skill only when the Burst is ready" }
                                 : new[] { "Combo*", "Hold only on the first rotation" })
                        CollectionAssert.Contains(texts, expected, $"{name}: {all}");

                    // Optional inputs: dashed and unfilled. Varka: Durin's Q, Prune's D, Nicole's whole turn; Chiori: Itto's Q.
                    var dashed = Descendants<System.Windows.Shapes.Shape>(strip).Where(Shown).Where(s => s.StrokeDashArray?.Count > 0).ToList();
                    Assert.AreEqual(varka ? 4 : 1, dashed.Count, $"{name}: dashed outlines");
                    Assert.IsTrue(dashed.All(s => s.Fill is SolidColorBrush b && b.Color.A == 0), $"{name}: an optional chip is unfilled");

                    // Tooltips: the game's English terms, the flag words in the interface's language.
                    var tips = Descendants<Grid>(strip).Where(Shown).Select(g => g.ToolTip as string).Where(t => t != null).ToList();
                    string tipList = string.Join(" | ", tips);
                    foreach (string ending in new[] { polish ? "(opcjonalnie)" : "(optional)", polish ? "(do wyboru)" : "(either)", " ×2 (hold)" })
                        Assert.IsTrue(tips.Any(t => t.EndsWith(ending, StringComparison.Ordinal)), $"{name}: no tooltip ends with {ending}: {tipList}");

                    // Never wider than the card: every turn sits inside the strip, notes included.
                    foreach (var turn in Descendants<DockPanel>(strip).Where(Shown))
                    {
                        var box = turn.TransformToAncestor(strip).TransformBounds(new Rect(turn.RenderSize));
                        Assert.IsTrue(box.Right <= strip.ActualWidth + 0.5, $"{name}: a turn ends at {box.Right:0.#} in a {strip.ActualWidth:0.#} px strip");
                    }

                    var vm = (CharacterOverviewViewModel)view.DataContext;
                    var chips = vm.Teams.SelectMany(t => t.Rotations).SelectMany(r => r.Steps).SelectMany(s => s.Chips).Where(c => c.IsIcon).ToList();
                    icons.Add($"{name}: {chips.Count(c => c.IconPath != null)} of {chips.Count} icon chips and " +
                              $"{chips.Count(c => c.AltIconPath != null)} of {chips.Count(c => c.IsPair)} second icons drawn from cached skill icons");

                    var full = Snapshot(host);
                    written.Add(Save(full, name + ".png"));
                    written.Add(Save(Crop(full, teams, host), name + "-team.png"));
                    written.Add(Save(Crop(Snapshot(host, 2), teams, host, scale: 2), name + "-team@2x.png"));
                }
            });
            foreach (string line in icons) Console.WriteLine(line);
            foreach (string p in written) Console.WriteLine(p);
        }

        private static (Border Host, CharacterOverviewView View) GoalHost(CharacterOverviewModel model, bool dark, bool popup, string lang)
        {
            var view = new CharacterOverviewView(model);
            var host = new Border
            {
                Background = new SolidColorBrush(dark ? Color.FromRgb(0x0B, 0x0D, 0x17) : Colors.White),
                Child = view,
                UseLayoutRounding = true,
            };
            if (dark) TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterPalette{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterLayout{(popup ? "Popup" : "Page")}.xaml"));
            Layout(host, popup ? PopupWidth : DetailWidth);
            return (host, view);
        }

        // The element's box on the whole-page render, with a margin, for a close-up; scale matches a Snapshot taken at that scale.
        private static BitmapSource Crop(BitmapSource full, FrameworkElement element, FrameworkElement host, double pad = 12, double scale = 1)
        {
            var box = element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));
            box.Inflate(pad, pad);
            box = new Rect(box.X * scale, box.Y * scale, box.Width * scale, box.Height * scale);
            box.Intersect(new Rect(0, 0, full.PixelWidth, full.PixelHeight));
            var crop = new CroppedBitmap(full, new Int32Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height));
            crop.Freeze();
            return crop;
        }

        // ── hosts ───────────────────────────────────────────────────────────

        private static BitmapSource RenderView(CharacterOverviewModel model, bool dark, string lang, bool expand)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var view = new CharacterOverviewView(model);
            long built = clock.ElapsedMilliseconds;
            var host = new Border
            {
                Background = new SolidColorBrush(dark ? Color.FromRgb(0x0B, 0x0D, 0x17) : Colors.White),
                Child = view,
                UseLayoutRounding = true,
            };
            if (dark) TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterPalette{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary($"Resources/CharacterLayout{(dark ? "Popup" : "Page")}.xaml"));
            double width = dark ? PopupWidth : DetailWidth;
            clock.Restart();
            host.Width = width;
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            long firstFrame = clock.ElapsedMilliseconds; // before the deferred sections
            Layout(host, width);
            Console.WriteLine($"{model.Name} {(dark ? "popup" : "page")}: view built in {built} ms, first frame layout {firstFrame} ms, all sections {clock.ElapsedMilliseconds} ms");
            if (expand)
            {
                foreach (var items in Descendants<ItemsControl>(view).Where(Shown))
                {
                    var first = Descendants<ToggleButton>(items).FirstOrDefault(t => t.IsEnabled && Shown(t) && t.IsChecked != true);
                    if (first != null) first.IsChecked = true;
                }
                Layout(host, width);
            }
            return Snapshot(host);
        }

        internal static CharactersPage Page(CharacterCatalog catalog, string select, out Border host, bool dark = false)
        {
            var page = new CharactersPage
            {
                GameSource = () => "Genshin", PackSource = () => null, PackReady = () => true, FetchImages = Fetch,
                LookupOverride = catalog.IsEmpty ? null : PackLookup.For("genshin", catalog),
            };
            // As in Settings: the app palette (ThemeService's slot), the control styles and the strings come from the
            // host, which has them before the page joins it.
            host = new Border { Padding = new Thickness(20, 16, 20, 12), UseLayoutRounding = true };
            host.Resources.MergedDictionaries.Add(Dictionary($"Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Resources/ModernStyles.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml")); // App.xaml merges it in the app: the state component's styles
            foreach (var d in Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);
            host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            host.Child = page;
            if (catalog.IsEmpty) return page;

            var load = page.LoadAsync("Genshin", _ => Task.FromResult(catalog));
            Pump(() => load.IsCompleted);
            Console.WriteLine($"page load: completed={load.IsCompleted} faulted={load.IsFaulted} state={page.State} items={((ListBox)page.FindName("CharacterList")).Items.Count} {load.Exception?.GetBaseException().Message}");
            var list = (ListBox)page.FindName("CharacterList");
            var item = list.Items.Cast<CharacterListItem>().FirstOrDefault(i => i.Name == select);
            if (item != null)
            {
                list.SelectedItem = item;
                list.ScrollIntoView(item);
            }
            var hostContent = (ContentControl)page.FindName("OverviewHost");
            Pump(() => (hostContent.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel vm && vm.Name == select);
            return page;
        }

        internal static void Pump(Func<bool> done)
        {
            for (int i = 0; i < 400 && !done(); i++)
            {
                DoEvents();
                System.Threading.Thread.Sleep(10);
            }
        }

        private static bool Shown(UIElement e)
        {
            for (DependencyObject d = e; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            return true;
        }

        // ── models ──────────────────────────────────────────────────────────

        internal static CharacterCatalog LoadCatalog(string root, string game, string injectAiFor = null)
        {
            string dir = Path.Combine(root, "tools", "out", game);
            string file = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "characters-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (file == null) return null;
            string json = File.ReadAllText(file);
            if (injectAiFor != null) json = InjectAi(json, injectAiFor);
            return CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), game);
        }

        private static CharacterOverviewModel BuildModel(string root, Sample sample)
        {
            var catalog = LoadCatalog(root, sample.Game, sample.InjectAi ? sample.Character : null);
            var record = catalog?.Characters.FirstOrDefault(c => c.Name == sample.Character);
            if (record == null) return null;
            var model = CharacterOverviewBuilder.Build(record, catalog, PackLookup.For(sample.Game, catalog));
            if (sample.NoImages) return model;

            var cache = CharacterImageCache.ForGame(ProfileId(sample.Game));
            CharacterIcons.ApplyCached(model, cache);
            if (sample.InjectAi) model.AmbiencePath = AmbienceFixture(model.ElementKey, sample.Game);
            if (Fetch) Task.Run(() => CharacterIcons.FetchMissingAsync(model, cache)).Wait(TimeSpan.FromSeconds(60));
            if (sample.NoSplash) model.SplashPath = null;
            CharacterOverviewViewModel.PrepareVisuals(model);
            return model;
        }

        private static string ProfileId(string wire) =>
            wire == "starrail" ? "StarRail" : wire == "zzz" ? "ZZZ" : "Genshin";

        /// <summary>
        /// Stand-in for the updater's ambience image (an element-mood picture, no likeness or text): soft light in the
        /// element's colour over ink, drawn here so the test needs no binary fixture.
        /// </summary>
        private static string AmbienceFixture(string element, string game)
        {
            string path = Path.Combine(OutputDir, "fixtures", $"ambience-{element}.png");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var hue = ElementPalette.For(game, element);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x0F, 0x12, 0x20), Color.FromRgb(0x18, 0x1C, 0x2A), 90), null, new Rect(0, 0, 640, 360));
                var rnd = new Random(7);
                for (int i = 0; i < 14; i++)
                {
                    var glow = new RadialGradientBrush(Color.FromArgb((byte)rnd.Next(40, 120), hue.R, hue.G, hue.B), Color.FromArgb(0, hue.R, hue.G, hue.B));
                    double r = rnd.Next(60, 220);
                    dc.DrawEllipse(glow, null, new Point(rnd.Next(0, 640), rnd.Next(80, 360)), r, r * 0.7);
                }
                dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 11, 13, 23), 90), null, new Rect(0, 180, 640, 180));
            }
            var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        /// <summary>Adds an `ai` block (contract: plan, Phases C2-D) to one character, from real ids.</summary>
        private static string InjectAi(string json, string character)
        {
            var root = JsonNode.Parse(json).AsObject();
            string SetId(string name) => root["sets"].AsObject().FirstOrDefault(p => (string)p.Value["name"] == name).Key;
            string WeaponId(string name) => root["weapons"].AsObject().FirstOrDefault(p => (string)p.Value["name"] == name).Key;
            string CharId(string name) => root["characters"].AsArray().FirstOrDefault(c => (string)c["name"] == name)?["id"]?.ToString();
            JsonArray Ids(params string[] ids) => new JsonArray(ids.Where(i => i != null).Select(i => (JsonNode)i).ToArray());
            JsonObject Text(string en, string pl) => new JsonObject { ["en"] = en, ["pl"] = pl };

            var target = root["characters"].AsArray().First(c => (string)c["name"] == character).AsObject();
            string self = (string)target["id"];
            target["ai"] = new JsonObject
            {
                ["model"] = "gpt-6.1-terra",
                ["generated_at"] = "2026-11-02",
                ["facts_sha"] = "sample",
                ["en"] = new JsonObject
                {
                    ["skills"] = new JsonObject
                    {
                        ["0"] = "Up to four quick sword strikes; the charged attack is a fast flurry.",
                        ["1"] = "Dashes in and leaves the Tamoto puppet behind to keep slashing nearby enemies.",
                    },
                    ["playstyle"] = new JsonArray("Press E, then swap out. Tamoto keeps fighting on its own."),
                    ["build_note"] = "The game has no build for Chiori. This is the usual pick, so check it against your artifacts.",
                },
                ["pl"] = new JsonObject
                {
                    ["skills"] = new JsonObject
                    {
                        ["0"] = "Do czterech szybkich cięć; atak naładowany to krótka seria.",
                        ["1"] = "Doskok, a w miejscu Chiori zostaje lalka Tamoto, która dalej tnie wrogów obok.",
                        ["2"] = "Dwa cięcia po łuku, liczone z OBR i ATK naraz.",
                    },
                    ["playstyle"] = new JsonArray("Wciśnij E i od razu zmień postać. Tamoto walczy dalej sama.",
                        "Weź do drużyny drugą postać Geo, a dostaniesz drugą lalkę.",
                        "Liczy się OBR i ATK, więc nie pakuj wszystkiego w jedno."),
                    ["build_note"] = "Gra nie podaje buildu dla Chiori. To typowy wybór graczy, sprawdź go ze swoimi artefaktami.",
                },
                ["build"] = new JsonObject
                {
                    ["sets"] = new JsonArray(new JsonObject { ["pieces"] = 4, ["ids"] = Ids(SetId("Golden Troupe"), SetId("Husk of Opulent Dreams")) }),
                    ["main_stats"] = new JsonArray(
                        new JsonObject { ["slot"] = "sands", ["stats"] = Ids("FIGHT_PROP_DEFENSE_PERCENT") },
                        new JsonObject { ["slot"] = "goblet", ["stats"] = Ids("FIGHT_PROP_ROCK_ADD_HURT", "FIGHT_PROP_DEFENSE_PERCENT") },
                        new JsonObject { ["slot"] = "circlet", ["stats"] = Ids("FIGHT_PROP_CRITICAL", "FIGHT_PROP_CRITICAL_HURT") }),
                    ["sub_stats"] = Ids("FIGHT_PROP_CRITICAL", "FIGHT_PROP_CRITICAL_HURT", "FIGHT_PROP_DEFENSE_PERCENT", "FIGHT_PROP_ATTACK_PERCENT"),
                    ["weapons"] = Ids(WeaponId("Uraku Misugiri"), WeaponId("Harbinger of Dawn")),
                    ["teammates"] = Ids(CharId("Navia"), CharId("Zhongli"), CharId("Albedo")),
                },
                ["image"] = new JsonObject
                {
                    ["url"] = "https://files.kaption.one/content/characters/genshin/img/" + self + ".jpg",
                    ["w"] = 1024, ["h"] = 576,
                    ["alt"] = Text("Stone dust in warm light over a quiet workshop", "Kamienny pył w ciepłym świetle nad cichą pracownią"),
                },
                ["teams"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = Text("Double Geo", "Podwójne Geo"),
                        ["source"] = "official",
                        ["members"] = Ids(self, CharId("Navia"), CharId("Zhongli"), CharId("Albedo")),
                        ["roles"] = new JsonObject
                        {
                            [self] = Text("Off-field DPS", "DPS z drugiej linii"),
                            [CharId("Navia")] = Text("Main DPS", "Główny DPS"),
                            [CharId("Zhongli")] = Text("Shield", "Tarcza"),
                            [CharId("Albedo")] = Text("Off-field Geo", "Geo z drugiej linii"),
                        },
                        ["why"] = Text("A second Geo character gives Chiori her second doll.",
                            "Druga postać Geo daje Chiori drugą lalkę, a Navia zbiera z tego całe obrażenia."),
                    },
                    new JsonObject
                    {
                        ["name"] = Text("Mono Geo", "Czyste Geo"),
                        ["source"] = "ai",
                        ["members"] = Ids(self, CharId("Navia"), CharId("Zhongli"), CharId("Gorou")),
                        ["roles"] = new JsonObject { [self] = Text("Sub DPS", "Drugi DPS"), [CharId("Gorou")] = Text("Buffer", "Wzmocnienia") },
                        ["why"] = Text("Gorou's buffs need a full Geo team. Navia works at C0; Gorou at C6 adds Geo CRIT DMG.",
                            "Gorou wzmacnia tylko pełną drużynę Geo. Navii wystarczy C0, a Gorou z C6 dokłada Geo CRIT DMG."),
                        // The rank the team assumes per member (teams v3): badges after the names.
                        ["ranks"] = new JsonObject { [CharId("Navia")] = 0, [CharId("Gorou")] = 6 },
                        // Rotations (round 3): two strips under the why, the second with a variant tag and a combo footnote.
                        ["rotations"] = JsonNode.Parse($$"""
                            [ { "variant": null,
                                "steps": [ { "member": "{{CharId("Zhongli")}}", "actions": [ { "a": "E" } ] },
                                           { "member": "{{CharId("Gorou")}}", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                                           { "member": "{{self}}", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                                           { "member": "{{CharId("Navia")}}", "actions": [ { "a": "Q" }, { "a": "E" }, { "a": "N", "n": 2 }, { "a": "E", "n": 2 } ] } ] },
                              { "variant": "Gorou",
                                "steps": [ { "member": "{{CharId("Gorou")}}", "actions": [ { "a": "E" }, { "a": "Q" } ] },
                                           { "member": "{{self}}", "actions": [ { "a": "E" } ] },
                                           { "member": "{{CharId("Navia")}}", "actions": [ { "a": "E" }, { "a": "combo" } ] } ],
                                "combo": { "{{CharId("Navia")}}": "N3 > E > N3 > E" } } ]
                            """),
                    }),
                // Goal stats (ai.stat_goals): the table in the build section.
                ["stat_goals"] = JsonNode.Parse(CharacterStatGoalsTests.ChioriGoals),
            };
            // Navia's team assumes Chiori at C1: the badge on Chiori's "Gra też w drużynach" row.
            root["characters"].AsArray().First(c => (string)c["name"] == "Navia").AsObject()["ai"] = new JsonObject
            {
                ["model"] = "gpt-6.1-terra",
                ["teams"] = new JsonArray(new JsonObject
                {
                    ["name"] = Text("Crystallize", "Krystalizacja"),
                    ["source"] = "ai",
                    ["members"] = Ids(CharId("Navia"), self, CharId("Zhongli")),
                    ["roles"] = new JsonObject { [self] = Text("Off-field DPS", "DPS z drugiej linii") },
                    ["why"] = Text("Built around Chiori at C1.", "Drużyna liczy na Chiori z C1."),
                    ["ranks"] = new JsonObject { [self] = 1 },
                }),
            };
            var skills = target["skills"].AsArray();
            JsonArray IndicesOf(params string[] kinds) => new JsonArray(skills
                .Select((s, i) => (kind: (string)s["kind"], i))
                .Where(x => kinds.Contains(x.kind)).Select(x => (JsonNode)x.i).Take(2).ToArray());
            target["leveling"] = IndicesOf("skill", "ultimate");
            target["ai"]["priority"] = new JsonObject
            {
                ["sets"] = Ids(SetId("Golden Troupe")),
                ["weapons"] = Ids(WeaponId("Uraku Misugiri")),
                ["progression"] = IndicesOf("constellation"),
            };
            target["ai"]["pl"]["guide"] = new JsonArray(
                new JsonObject { ["heading"] = "W skrócie", ["body"] = "Chiori to DPS Geo z drugiej linii. Umiejętnością stawia lalkę Tamoto, która tnie wrogów, gdy na polu stoi już ktoś inny.\n\nNajlepiej czuje się obok drugiej postaci Geo: wtedy dostaje drugą lalkę i jej obrażenia rosną prawie dwa razy." },
                new JsonObject { ["heading"] = "Jak działają umiejętności", ["body"] = "Doskok, lalka, zmiana postaci. Tamoto atakuje sama przez kilkanaście sekund.\n- Druga postać Geo w drużynie daje drugą lalkę.\n- Wybuch zadaje obrażenia z OBR i ATK naraz." },
                new JsonObject { ["heading"] = "Build i dlaczego taki", ["body"] = "Golden Troupe podbija obrażenia umiejętności, a Chiori prawie cały czas jest poza polem. Klepsydra z OBR%, kielich Geo, diadem na kryty." },
                new JsonObject { ["heading"] = "Częste błędy", ["body"] = "- Trzymanie Chiori na polu: jej siła to lalka, nie ataki podstawowe.\n- Zapominanie o drugiej postaci Geo." });
            target["ai"]["en"]["guide"] = new JsonArray(
                new JsonObject { ["heading"] = "Overview", ["body"] = "Chiori is an off-field Geo damage dealer. Her skill drops the Tamoto doll, which keeps slashing while someone else is on the field." },
                new JsonObject { ["heading"] = "How the kit works", ["body"] = "Dash in, leave the doll, swap out.\n- A second Geo character gives a second doll." });
            return root.ToJsonString();
        }

        /// <summary>
        /// Polish text for previews, from the local PL pack joined to the installed English TextMap.
        /// Exact match only, like MatcherTranslationLookup. Null (English) when either file is missing.
        /// </summary>
        internal sealed class PackLookup : ITranslationLookup
        {
            private static readonly Dictionary<string, PackLookup> Cache = new Dictionary<string, PackLookup>();
            private readonly Dictionary<string, string> _map;
            private PackLookup(Dictionary<string, string> map) => _map = map;

            public bool TryTranslateExact(string english, out string translated) =>
                _map.TryGetValue(MatcherTranslationLookup.PlainText(english), out translated);

            public static ITranslationLookup For(string game, CharacterCatalog catalog)
            {
                if (game != "genshin") return null;
                if (Cache.TryGetValue(game, out var cached)) return cached;
                string en = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption", "Genshin", "TextMapEN.json");
                string pl = Path.Combine(RepoRoot() ?? "", "tools", "out", "TextMapPL-6.6-final.json");
                if (!File.Exists(en) || !File.Exists(pl)) return Cache[game] = null;

                // The app's plain form: markup, '#', device wording and the literal "\n" style removed.
                string Plain(string s) => MatcherTranslationLookup.PlainText(s);
                var needed = new HashSet<string>(StringComparer.Ordinal);
                void Add(string s) { if (!string.IsNullOrEmpty(s)) needed.Add(Plain(s)); }
                foreach (var c in catalog.Characters)
                {
                    Add(c.Name); Add(c.Element); Add(c.WeaponType); Add(c.Role?.Template);
                    foreach (var s in c.Skills) { Add(s.Name); Add(s.Short?.Template); Add(s.Description?.Template); }
                }
                foreach (var s in catalog.Sets.Values) { Add(s.Name); foreach (var b in s.Bonuses) Add(b.Text?.Template); }
                foreach (var w in catalog.Weapons.Values) { Add(w.Name); Add(w.PassiveName); Add(w.Passive?.Template); }
                foreach (var label in catalog.Stats.Values) Add(label);

                // Every id per English string: the first one the PL pack translates wins.
                var ids = new Dictionary<string, string>(StringComparer.Ordinal); // id -> english
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(en)))
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && needed.Contains(Plain(p.Value.GetString())))
                            ids[p.Name] = Plain(p.Value.GetString());
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(pl)))
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && ids.TryGetValue(p.Name, out string english) &&
                            !map.ContainsKey(english) && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                            map[english] = MatcherTranslationLookup.ResolvePlaceholders(p.Value.GetString());
                return Cache[game] = new PackLookup(map);
            }
        }
    }
}
