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
    /// Inconclusive when tools/out holds no character data.
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
                        ["why"] = Text("Gorou's buffs need a full Geo team.", "Gorou wzmacnia tylko pełną drużynę Geo."),
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
