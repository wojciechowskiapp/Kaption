using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using GI_Subtitles.Services.Characters;

namespace GI_Subtitles.Views
{
    /// <summary>Display sizes in DIP; images are decoded at twice these, once, and shared through IconImages.</summary>
    public static class OverviewImageSizes
    {
        public const int Portrait = 84;
        public const int Skill = 40;
        public const int Rank = 36;
        public const int Set = 24;
        public const int Weapon = 40;
        public const int Member = 44;
        public const int Material = 20;
        public const int Tip = 28;
        public const int Fact = 32;
        public const int Ambience = 300;
        /// <summary><see cref="Ambience"/> as a layout width (XAML needs a double).</summary>
        public const double AmbienceWidth = Ambience;
        /// <summary>Hero art: splash decoded to this height (the band shows its upper half), portrait to this size.</summary>
        public const int SplashDecodeHeight = 760;
        public const int HeroPortrait = 200;
        /// <summary>The ambience image behind the header, decoded at the updater's own height (1216×688 today).</summary>
        public const int HeroAmbienceDecodeHeight = 688;
    }

    /// <summary>
    /// Display-ready wrapper over <see cref="CharacterOverviewModel"/>, in the page's reading order: hero, at a glance,
    /// build, skills, ranks, teams, materials, guide, links. The service model stays free of WPF types.
    /// </summary>
    public sealed class CharacterOverviewViewModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, string> ProgressionLetters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["constellation"] = "C", ["eidolon"] = "E", ["mindscape"] = "M",
            };

        /// <summary>"Gra też w drużynach" shows this many hosts until "Pokaż więcej".</summary>
        public const int TeamHostCap = 6;

        private readonly Color _hue;
        private ImageSource _heroArt;
        private ImageSource _heroAmbience;
        private bool _darkSurface;
        private bool _allTeamHostsShown;
        private bool _canOpenCharacters;
        private string _siteLanguage = "pl";
        private RotationWords _rotationWords = RotationWords.English;

        public CharacterOverviewViewModel(CharacterOverviewModel model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            Game = model.Game.Length > 0 ? model.Game : InferGame(model);

            _hue = ElementPalette.For(Game, model.ElementKey);
            ElementFill = ElementPalette.Brush(_hue);
            ElementInk = ElementPalette.Brush(ElementPalette.InkOn(_hue));
            ElementSoft = ElementPalette.Brush(_hue, 0x40);
            ElementWash = ElementPalette.Brush(_hue, 0x1F);
            HeroWash = Wash(_hue);
            HeroTint = Tint(_hue);

            var skills = model.Skills.Where(s => !ProgressionLetters.ContainsKey(s.Kind ?? "")).ToList();
            Skills = skills.Select((s, i) => SkillRow.From(s, showKind: i == 0 || skills[i - 1].Kind != s.Kind)).ToList();
            HasAiSummaries = skills.Any(s => s.AiSummary.Length > 0);

            var progression = model.Skills.Where(s => ProgressionLetters.ContainsKey(s.Kind ?? "")).ToList();
            Progression = progression
                .Select((s, i) => new ProgressionRow(ProgressionLetters[s.Kind] + (i + 1), s,
                    isFirst: i == 0, isLast: i == progression.Count - 1))
                .ToList();
            ProgressionKey = progression.Count > 0 ? "Char_Tab_" + progression[0].Kind.ToLowerInvariant() : "";

            SetGroups = model.SetGroups.Select(SetGroupRow.From).Where(g => g.Options.Count > 0).ToList();
            // Stat, set, weapon and material names read as the game shows them; the translation is a gloss.
            MainStats = model.MainStats.Where(m => m.Stats.Count > 0).Select(m =>
            {
                var english = m.Stats.Select((s, i) => ShortStatName(i < m.EnglishStats.Count && m.EnglishStats[i].Length > 0 ? m.EnglishStats[i] : s)).ToList();
                return new MainStatRow
                {
                    SlotKey = m.SlotKey,
                    Icon = SlotIcons.For(m.Slot),
                    Primary = english[0],
                    Alternatives = english.Count > 1 ? " / " + string.Join(" / ", english.Skip(1)) : "",
                    Gloss = GlossOf(english, GlossInputs(english, m.Stats), " / "),
                };
            }).ToList();
            SubStats = model.SubStats.Select((s, i) =>
            {
                string english = ShortStatName(i < model.SubStatsEnglish.Count && model.SubStatsEnglish[i].Length > 0 ? model.SubStatsEnglish[i] : s);
                return new RankedStat { Rank = i + 1, Name = english, Gloss = GlossOf(new[] { english }, GlossInputs(new[] { english }, new[] { s }), "") };
            }).ToList();
            Weapons = model.Weapons.Select(w => new WeaponRow(w, Game)).ToList();
            StatGoals = StatGoalRow.For(model.StatGoals, _siteLanguage);

            // When the whole build is an AI pick the section says so once; otherwise each AI line does.
            Tips = model.Highlights.Select(h => new TipRow(h, Game) { ShowAiTag = h.IsAi && !(model.HasBuild && model.BuildIsAi) }).ToList();
            MaterialsAscension = model.MaterialsAscension.Select(m => new MaterialRow(m)).ToList();
            MaterialsSkills = model.MaterialsSkills.Select(m => new MaterialRow(m)).ToList();
            Links = model.Links.Select(l => new LinkRow(l.Label, l.Url, l.IconKey, l.Monogram)).ToList();

            // The guide's first section is the summary "at a glance"; the rest is the article further down.
            if (model.Guide.Count > 0)
            {
                Summary = GuideBlock.Parse(model.Guide[0].Body);
                Guide = model.Guide.Skip(1).Select((g, i) => new GuideRow(g.Heading, g.Body, expanded: i == 0)).ToList();
            }
            else
            {
                Summary = model.Playstyle.Select(p => new GuideBlock(p, bullet: true)).ToList();
                Guide = Array.Empty<GuideRow>();
            }

            Facts = BuildFacts(model, Skills);
            Teams = model.Teams.Select(t => new TeamCardRow(t, _rotationWords)).ToList();
            TeamHosts = model.TeamHosts.Select(h => new TeamHostRow(h)).ToList();
            TeammateCards = model.TeammateCards.Count > 0
                ? model.TeammateCards.Select(t => new TeammateChip(t.Name, t.IconPath)).ToList()
                : model.Teammates.Select(n => new TeammateChip(n, null)).ToList();
            Sections = BuildSections();

            _heroArt = HeroArtCached(model);
            if (_heroArt == null && (model.SplashPath != null || model.IconPath != null))
                Later(() => LoadHeroArt(model), a => { _heroArt = a; Changed(nameof(HeroArt)); Changed(nameof(HasSplash)); Changed(nameof(HasPortraitArt)); Changed(nameof(HasNoArt)); });
            if (HasAmbience)
            {
                // The header's backdrop: on disk next to the bundle once fetched; until then (or offline) the plain wash.
                _heroAmbience = IconImages.LargeCachedOrNull(model.AmbiencePath, OverviewImageSizes.HeroAmbienceDecodeHeight);
                if (_heroAmbience == null)
                    Later(() => IconImages.LoadLarge(model.AmbiencePath, OverviewImageSizes.HeroAmbienceDecodeHeight),
                        a => { _heroAmbience = a; Changed(nameof(HeroAmbience)); Changed(nameof(HasHeroAmbience)); });
            }
        }

        /// <summary>
        /// Renders the backdrop and decodes every image the overview shows, so the view builds without disk or
        /// decode work on the UI thread. Safe on any thread; hosts call it next to the model build.
        /// </summary>
        public static void PrepareVisuals(CharacterOverviewModel model)
        {
            if (model == null) return;
            LoadHeroArt(model);
            IconImages.Load(model.IconPath, OverviewImageSizes.Portrait);
            IconImages.LoadLarge(model.AmbiencePath, OverviewImageSizes.HeroAmbienceDecodeHeight);
            foreach (var s in model.Skills)
                IconImages.Load(s.IconPath, ProgressionLetters.ContainsKey(s.Kind ?? "") ? OverviewImageSizes.Rank : OverviewImageSizes.Skill);
            foreach (var o in model.SetGroups.SelectMany(g => g.Options)) IconImages.Load(o.IconPath, OverviewImageSizes.Set);
            foreach (var w in model.Weapons) IconImages.Load(w.IconPath, OverviewImageSizes.Weapon);
            foreach (var m in model.Teams.SelectMany(t => t.Members)) IconImages.Load(m.IconPath, OverviewImageSizes.Member);
            // Rotation portraits are the card's member images; skill chips share the skills list's decode size.
            foreach (var s in model.Teams.SelectMany(t => t.Rotations).SelectMany(r => r.Steps))
            {
                IconImages.Load(s.IconPath, OverviewImageSizes.Member);
                foreach (var a in s.Actions)
                {
                    IconImages.Load(a.IconPath, OverviewImageSizes.Skill);
                    IconImages.Load(a.AltIconPath, OverviewImageSizes.Skill);
                }
            }
            foreach (var m in model.TeammateCards) IconImages.Load(m.IconPath, OverviewImageSizes.Member);
            foreach (var h in model.TeamHosts) IconImages.Load(h.IconPath, OverviewImageSizes.Member);
            foreach (var h in model.Highlights) IconImages.Load(h.IconPath, OverviewImageSizes.Tip);
            foreach (var m in model.MaterialsAscension.Concat(model.MaterialsSkills)) IconImages.Load(m.IconPath, OverviewImageSizes.Material);
        }

        // Full-body art when it is on disk, else the portrait, large.
        private static ImageSource LoadHeroArt(CharacterOverviewModel model) =>
            model.SplashPath != null
                ? IconImages.LoadLarge(model.SplashPath, OverviewImageSizes.SplashDecodeHeight)
                : IconImages.Load(model.IconPath, OverviewImageSizes.HeroPortrait);

        private static ImageSource HeroArtCached(CharacterOverviewModel model) =>
            model.SplashPath != null
                ? IconImages.LargeCachedOrNull(model.SplashPath, OverviewImageSizes.SplashDecodeHeight)
                : IconImages.CachedOrNull(model.IconPath, OverviewImageSizes.HeroPortrait);

        // A soft tint of the element over the page surface, fading to the right where the art sits.
        private static Brush Wash(Color hue)
        {
            var brush = new LinearGradientBrush(Color.FromArgb(0x24, hue.R, hue.G, hue.B), Color.FromArgb(0x08, hue.R, hue.G, hue.B), 0);
            brush.Freeze();
            return brush;
        }

        // Over the ambience image: the element rises from the lower half, where the header meets the panels below.
        private static Brush Tint(Color hue)
        {
            var brush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, hue.R, hue.G, hue.B), 0.35));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, hue.R, hue.G, hue.B), 0.8));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1F, hue.R, hue.G, hue.B), 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The interface language the page link follows ("pl" opens the Polish page, anything else the English one).
        /// The view reads it from the strings in use, so a host and its tests agree.
        /// </summary>
        internal void SetSiteLanguage(string language)
        {
            if (string.IsNullOrEmpty(language) || string.Equals(language, _siteLanguage, StringComparison.OrdinalIgnoreCase)) return;
            _siteLanguage = language;
            Changed(nameof(PageUrl));
            Changed(nameof(HasPageUrl));
            // The goal values and notes are written in the interface's language too.
            StatGoals = StatGoalRow.For(Model.StatGoals, language);
            Changed(nameof(StatGoals));
        }

        /// <summary>The flagged rotation chips' tooltip words, from the strings in use; the team cards are rebuilt when they change.</summary>
        internal void SetRotationWords(RotationWords words)
        {
            if (words == null || words == _rotationWords) return;
            _rotationWords = words;
            if (!Model.Teams.Any(t => t.Rotations?.Count > 0)) return;
            Teams = Model.Teams.Select(t => new TeamCardRow(t, words)).ToList();
            Changed(nameof(Teams));
        }

        /// <summary>The host's surface: element text and icons are darkened on light, lightened on dark.</summary>
        internal void SetSurface(bool dark)
        {
            if (_darkSurface == dark) return;
            _darkSurface = dark;
            Changed(nameof(ElementText));
        }

        public void SetActiveSection(string key)
        {
            foreach (var s in Sections) s.IsActive = s.Key == key;
        }

        private void Later<T>(Func<T> work, Action<T> apply) where T : class
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Run(work).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    dispatcher.BeginInvoke(new Action(() => apply(t.Result)));
            }, TaskScheduler.Default);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public CharacterOverviewModel Model { get; }

        /// <summary>Wire id inferred from the data (genshin / starrail / zzz), "" when unclear.</summary>
        public string Game { get; }

        // ── hero ──
        public string Name => Model.Name;
        /// <summary>Secondary line under the name: the Polish protagonist title, else empty.</summary>
        public string NameGloss => Model.NameGloss ?? "";
        public string Initial => InitialOf(Model.Name);
        public string IconPath => Model.IconPath;
        public string RarityText => Model.RarityText;
        public string RarityTier => Tier(Game, Model.Rarity);
        /// <summary>ZZZ shows a rank letter (S/A/B) in a tile; the other games show stars.</summary>
        public bool IsRank => RarityText.Length == 1 && char.IsLetter(RarityText[0]);
        public bool ShowStars => RarityText.Length > 0 && !IsRank;
        public string Element => Model.Element;
        public string WeaponType => Model.WeaponType;
        public string Role => Model.Role;
        public string GameVersion => Model.GameVersion;

        public Brush ElementFill { get; }
        public Brush ElementInk { get; }
        public Brush ElementSoft { get; }
        /// <summary>A faint element tint for icon discs.</summary>
        public Brush ElementWash { get; }
        public Brush HeroWash { get; }
        /// <summary>The element colour, readable as text and icons on the host's surface.</summary>
        public Brush ElementText => ElementPalette.Brush(ElementPalette.Readable(_hue, _darkSurface));

        /// <summary>The character's ambience image behind the header (null until it is on disk and decoded).</summary>
        public ImageSource HeroAmbience => _heroAmbience;
        public bool HasHeroAmbience => _heroAmbience != null;
        /// <summary>Where the header's crop of the ambience image is centred (the data's focal point, 0 to 1).</summary>
        public System.Windows.Point AmbienceFocal => new System.Windows.Point(Model.AmbienceFocalX, Model.AmbienceFocalY);
        /// <summary>The element's colour rising from the header's lower half over the ambience image.</summary>
        public Brush HeroTint { get; }

        public ImageSource HeroArt => _heroArt;
        public bool HasSplash => _heroArt != null && Model.SplashPath != null;
        public bool HasPortraitArt => _heroArt != null && Model.SplashPath == null;
        public bool HasNoArt => _heroArt == null;
        /// <summary>Splash art is scaled past the band so its upper body fills it; full-body art differs per game.</summary>
        public double HeroArtZoom => Game == "genshin" ? 1.9 : Game == "starrail" ? 1.55 : 1.45;

        public IReadOnlyList<SectionLink> Sections { get; }

        // ── at a glance ──
        /// <summary>The AI summary (the guide's first section), else the AI playstyle bullets.</summary>
        public IReadOnlyList<GuideBlock> Summary { get; }
        public bool HasSummary => Summary.Count > 0;
        /// <summary>The ambience image is on disk. It is the header's backdrop (a theme for the page), not a picture
        /// in "W skrócie".</summary>
        public bool HasAmbience => !string.IsNullOrEmpty(Model.AmbiencePath);
        public string AmbienceAlt => Model.AmbienceAlt;
        public IReadOnlyList<FactTile> Facts { get; }
        public bool HasGlance => HasSummary || Facts.Count > 0;
        /// <summary>
        /// "W skrócie" is a heading (and a tab) only with something to read. The catalogs carry no official intro
        /// text today, so without an AI summary the key facts sit under the hero on their own.
        /// </summary>
        public bool ShowGlanceHeading => HasSummary;

        // ── skills and ranks ──
        public IReadOnlyList<SkillRow> Skills { get; }
        public bool HasSkills => Skills.Count > 0;
        public bool HasAiSummaries { get; }
        public IReadOnlyList<string> Playstyle => Model.Playstyle;
        public IReadOnlyList<ProgressionRow> Progression { get; }
        public bool HasProgression => Progression.Count > 0;
        public string ProgressionKey { get; }

        // ── build ──
        public bool HasBuild => Model.HasBuild;
        public bool IsOfficialBuild => Model.HasOfficialBuild;
        public bool IsAiBuild => Model.HasBuild && Model.BuildIsAi;
        public string BuildNote => IsAiBuild ? Model.BuildNote : "";
        public IReadOnlyList<SetGroupRow> SetGroups { get; }
        public IReadOnlyList<MainStatRow> MainStats { get; }
        public IReadOnlyList<RankedStat> SubStats { get; }
        public IReadOnlyList<WeaponRow> Weapons { get; }
        /// <summary>Stat values to aim for, in the interface's language; with or without a build.</summary>
        public IReadOnlyList<StatGoalRow> StatGoals { get; private set; }
        public bool HasStatGoals => StatGoals.Count > 0;
        public bool HasBuildSection => HasBuild || Tips.Count > 0 || HasStatGoals;

        // ── teams ──
        public IReadOnlyList<TeamCardRow> Teams { get; private set; }
        public bool HasTeamCards => Teams.Count > 0;
        public IReadOnlyList<string> Teammates => Model.Teammates;
        public IReadOnlyList<TeammateChip> TeammateCards { get; }
        /// <summary>The game's own teammates, shown only when there are no team comps.</summary>
        public bool ShowTeammates => Teams.Count == 0 && TeammateCards.Count > 0;
        public bool HasTeams => Teams.Count > 0 || TeammateCards.Count > 0 || TeamHosts.Count > 0;

        /// <summary>Other characters' teams with a place for this one, the newest host first ("Gra też w drużynach").</summary>
        public IReadOnlyList<TeamHostRow> TeamHosts { get; }
        public bool HasTeamHosts => TeamHosts.Count > 0;
        /// <summary>The first <see cref="TeamHostCap"/> hosts, or all of them after "Pokaż więcej".</summary>
        public IReadOnlyList<TeamHostRow> ShownTeamHosts =>
            _allTeamHostsShown || TeamHosts.Count <= TeamHostCap ? TeamHosts : TeamHosts.Take(TeamHostCap).ToList();
        /// <summary>How many hosts "Pokaż więcej" adds; 0 once they show.</summary>
        public int HiddenTeamHosts => TeamHosts.Count - ShownTeamHosts.Count;
        public bool HasHiddenTeamHosts => HiddenTeamHosts > 0;

        public void ShowAllTeamHosts()
        {
            if (_allTeamHostsShown) return;
            _allTeamHostsShown = true;
            Changed(nameof(ShownTeamHosts));
            Changed(nameof(HiddenTeamHosts));
            Changed(nameof(HasHiddenTeamHosts));
        }

        /// <summary>A host's name opens that character's page; only where a host can switch pages (the Characters tab).</summary>
        public bool CanOpenCharacters
        {
            get => _canOpenCharacters;
            internal set { if (_canOpenCharacters != value) { _canOpenCharacters = value; Changed(); } }
        }

        // ── tips, materials, guide, links ──
        public IReadOnlyList<TipRow> Tips { get; }
        public IReadOnlyList<MaterialRow> MaterialsAscension { get; }
        public IReadOnlyList<MaterialRow> MaterialsSkills { get; }
        public IReadOnlyList<LinkRow> Links { get; }
        public IReadOnlyList<GuideRow> Guide { get; }
        public bool HasTips => Model.HasTips;
        public bool HasGuide => Guide.Count > 0;
        public bool HasMaterials => MaterialsAscension.Count > 0 || MaterialsSkills.Count > 0;
        /// <summary>The character's own page on kaption.one in the interface's language (Polish or English).</summary>
        public string PageUrl => Model.PageUrlFor(_siteLanguage);
        public bool HasPageUrl => PageUrl.Length > 0;
        public bool HasLinks => HasPageUrl || Links.Count > 0;
        public string WeaponsKey => Game.Length > 0 ? "Char_Weapons_" + Game : "Char_Weapons";

        private IReadOnlyList<SectionLink> BuildSections()
        {
            var list = new List<SectionLink>();
            void Add(bool shown, string key, string labelKey) { if (shown) list.Add(new SectionLink(key, labelKey)); }
            Add(HasGlance && ShowGlanceHeading, "Glance", "Char_Nav_Glance");
            Add(HasBuildSection, "Build", "Char_Section_Build");
            Add(HasSkills, "Skills", "Char_Section_Skills");
            Add(HasProgression, "Ranks", ProgressionKey);
            Add(HasTeams, "Teams", "Char_Nav_Teams");
            Add(HasMaterials, "Materials", "Char_Nav_Materials");
            Add(HasGuide, "Guide", "Char_Tips_Guide");
            Add(HasLinks, "Links", "Char_Tips_Links");
            return list;
        }

        // Role, the signature skill (ultimate, else the skill) and what to level first.
        private static IReadOnlyList<FactTile> BuildFacts(CharacterOverviewModel model, IReadOnlyList<SkillRow> skills)
        {
            var facts = new List<FactTile>();
            if (!string.IsNullOrWhiteSpace(model.Role))
                facts.Add(new FactTile("Char_Fact_Role", Flatten(model.Role), null));
            var signature = skills.FirstOrDefault(s => s.Kind == "ultimate")
                            ?? skills.FirstOrDefault(s => s.Kind == "chain" && s.Title.StartsWith("Ultimate", StringComparison.OrdinalIgnoreCase))
                            ?? skills.FirstOrDefault(s => s.Kind == "skill");
            if (signature != null)
                facts.Add(new FactTile("Char_Fact_Signature", SkillMarkers.WithMarker(signature.Title, signature.Marker), signature));
            if (model.Leveling.Count > 0)
            {
                var first = skills.FirstOrDefault(s => s.Title == model.Leveling[0]);
                facts.Add(new FactTile("Char_Fact_LevelFirst", SkillMarkers.WithMarker(model.Leveling[0], first?.Marker), first)
                {
                    IsAi = model.LevelingIsAi,
                });
            }
            return facts;
        }

        /// <summary>"top" for 5 stars / rank S, "high" for 4 stars / rank A; drives the gold/violet colour.</summary>
        internal static string Tier(string game, int rarity)
        {
            int top = string.Equals(game, "zzz", StringComparison.OrdinalIgnoreCase) ? 4 : 5;
            return rarity >= top ? "top" : rarity == top - 1 ? "high" : "";
        }

        internal static string InitialOf(string name)
        {
            foreach (char c in name ?? "")
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }

        /// <summary>Flattens game text into one paragraph; short heading lines get a colon ("Normal Attack: ...").</summary>
        internal static string Flatten(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                sb.Append(line);
                if (i == lines.Count - 1) break;
                char last = line[line.Length - 1];
                bool heading = line.Length <= 40 && ".!?:;…)\"".IndexOf(last) < 0;
                sb.Append(heading ? ": " : " ");
            }
            return sb.ToString();
        }

        /// <summary>The game's short names for the percent stats ("ATK%", not "ATK Percentage"); every other name as given.</summary>
        internal static string ShortStatName(string english)
        {
            string s = english?.Trim() ?? "";
            if (string.Equals(s, "ATK Percentage", StringComparison.OrdinalIgnoreCase)) return "ATK%";
            if (string.Equals(s, "DEF Percentage", StringComparison.OrdinalIgnoreCase)) return "DEF%";
            if (string.Equals(s, "HP Percentage", StringComparison.OrdinalIgnoreCase)) return "HP%";
            return english;
        }

        private static bool IsShortStat(string name) => name is "ATK%" or "DEF%" or "HP%";

        /// <summary>A short name needs no gloss: its translation is left out of the gloss line.</summary>
        private static IReadOnlyList<string> GlossInputs(IReadOnlyList<string> english, IReadOnlyList<string> translated) =>
            english.Select((e, i) => IsShortStat(e) ? e : i < translated.Count ? translated[i] : e).ToList();

        /// <summary>The translated names joined, or "" when each reads the same as the English one.</summary>
        internal static string GlossOf(IReadOnlyList<string> english, IReadOnlyList<string> translated, string separator)
        {
            bool differs = false;
            var parts = new List<string>();
            for (int i = 0; i < english.Count; i++)
            {
                string t = i < translated.Count && !string.IsNullOrWhiteSpace(translated[i]) ? translated[i] : english[i];
                differs |= TranslatesAWord(english[i], t);
                parts.Add(t);
            }
            return differs ? string.Join(separator, parts) : "";
        }

        /// <summary>
        /// The web's rule for a gloss: worth a line only when it translates at least one word ("Procent ATK" for
        /// "ATK Percentage" does; "CRIT DMG" for "CRIT DMG" or a reordering of the same words does not).
        /// </summary>
        internal static bool TranslatesAWord(string english, string translated)
        {
            if (string.IsNullOrWhiteSpace(translated)) return false;
            var words = new HashSet<string>(Words(english), StringComparer.OrdinalIgnoreCase);
            return Words(translated).Any(w => !words.Contains(w));
        }

        private static IEnumerable<string> Words(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s ?? "", @"[\p{L}\p{N}]+").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value);

        /// <summary>Two names when the pack translates it: the English one the game shows first.</summary>
        internal static (string Title, string Subtitle) Names(string english, string translated)
        {
            string title = string.IsNullOrWhiteSpace(english) ? translated ?? "" : english;
            string sub = string.IsNullOrWhiteSpace(translated) || string.Equals(translated, title, StringComparison.OrdinalIgnoreCase)
                ? "" : translated;
            return (title, sub);
        }

        private static string InferGame(CharacterOverviewModel m)
        {
            var kinds = new HashSet<string>(m.Skills.Select(s => s.Kind ?? ""), StringComparer.OrdinalIgnoreCase);
            var slots = new HashSet<string>(m.MainStats.Select(s => s.Slot ?? ""), StringComparer.OrdinalIgnoreCase);
            if (kinds.Contains("constellation") || slots.Contains("sands") || slots.Contains("goblet")) return "genshin";
            if (kinds.Contains("eidolon") || kinds.Contains("technique") || slots.Contains("rope")) return "starrail";
            if (kinds.Contains("mindscape") || kinds.Contains("dodge") || slots.Contains("disc4")) return "zzz";
            return "";
        }
    }

    /// <summary>A jump target in the section tabs; the active one follows the scroll position.</summary>
    public sealed class SectionLink : INotifyPropertyChanged
    {
        private bool _isActive;

        public SectionLink(string key, string labelKey)
        {
            Key = key;
            LabelKey = labelKey;
        }

        public string Key { get; }
        public string LabelKey { get; }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>A paragraph or a bullet of AI text ("- " lines in the data).</summary>
    public sealed class GuideBlock
    {
        public GuideBlock(string text, bool bullet)
        {
            Text = text ?? "";
            IsBullet = bullet;
        }

        public string Text { get; }
        public bool IsBullet { get; }

        /// <summary>Blank lines split paragraphs; a line starting with "- " is a bullet.</summary>
        public static IReadOnlyList<GuideBlock> Parse(string body)
        {
            var blocks = new List<GuideBlock>();
            var paragraph = new StringBuilder();
            void Flush()
            {
                if (paragraph.Length > 0) blocks.Add(new GuideBlock(paragraph.ToString().Trim(), bullet: false));
                paragraph.Clear();
            }
            foreach (string raw in (body ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { Flush(); continue; }
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal))
                {
                    Flush();
                    blocks.Add(new GuideBlock(line.Substring(2).Trim(), bullet: true));
                    continue;
                }
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
            Flush();
            return blocks;
        }
    }

    /// <summary>One "at a glance" tile; a skill tile shows the skill's icon or kind glyph.</summary>
    public sealed class FactTile
    {
        internal FactTile(string labelKey, string text, SkillRow skill)
        {
            LabelKey = labelKey;
            Text = text ?? "";
            IconPath = skill?.IconPath;
            Glyph = skill?.Glyph;
            HasSkill = skill != null;
        }

        public string LabelKey { get; }
        public string Text { get; }
        public string IconPath { get; }
        public Geometry Glyph { get; }
        public bool HasSkill { get; }
        public bool IsAi { get; internal set; }
    }

    /// <summary>A row that opens in place.</summary>
    public abstract class ExpandableRow : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                Changed();
                OnExpandedChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnExpandedChanged() { }

        protected void Changed([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class SkillRow : ExpandableRow
    {
        private bool _previewIsDescription;

        public string Kind { get; private set; } = "";
        public string KindKey { get; private set; } = "";
        public bool ShowKind { get; private set; }
        /// <summary>The translated name (pack), else English.</summary>
        public string Name { get; private set; } = "";
        /// <summary>The English name the game shows: the reference.</summary>
        public string Title { get; private set; } = "";
        /// <summary>The translation under it, "" when the pack has none or it matches.</summary>
        public string Subtitle { get; private set; } = "";
        /// <summary>"(E)", "(Q)", …: written after the name wherever it appears outside this card.</summary>
        public string Marker { get; private set; } = "";
        public string IconPath { get; private set; }
        public Geometry Glyph { get; private set; }
        /// <summary>AI line, else the official short text, else the start of the description.</summary>
        public string Preview { get; private set; } = "";
        public bool PreviewIsAi { get; private set; }
        /// <summary>The preview is just the start of the description: one line is enough.</summary>
        public bool IsTeaser => _previewIsDescription;
        public string Description { get; private set; } = "";
        public bool CanExpand { get; private set; }
        public bool ShowPreview => Preview.Length > 0 && !(IsExpanded && _previewIsDescription);
        public bool ShowDescription => IsExpanded && Description.Length > 0;
        /// <summary>The pack has no Polish for this text: it shows in English, and says so.</summary>
        public bool IsUntranslated { get; private set; }
        /// <summary>Marked only while English is on screen: not under a Polish AI line or short text.</summary>
        public bool ShowUntranslated => IsUntranslated && (IsExpanded || _previewIsDescription);

        internal static SkillRow From(OverviewSkill s, bool showKind)
        {
            string Flatten(string text) => CharacterOverviewViewModel.Flatten(text);
            string flat = Flatten(s.Description);
            bool ai = s.AiSummary.Length > 0;
            // Previews read as one paragraph; the full text keeps the game's line breaks.
            string preview = ai ? Flatten(s.AiSummary) : s.Short.Length > 0 ? Flatten(s.Short) : flat;
            bool previewIsDescription = !ai && s.Short.Length == 0;
            var (title, subtitle) = CharacterOverviewViewModel.Names(s.EnglishName, s.Name);
            return new SkillRow
            {
                Kind = s.Kind ?? "",
                KindKey = s.KindKey,
                ShowKind = showKind,
                Name = s.Name,
                Title = title,
                Subtitle = subtitle,
                Marker = s.Marker ?? "",
                IconPath = s.IconPath,
                Glyph = SkillGlyphs.For(s.Kind),
                Preview = preview,
                PreviewIsAi = ai,
                Description = s.Description,
                IsUntranslated = s.DescriptionInEnglish,
                _previewIsDescription = previewIsDescription,
                // Always openable: whether a one-line teaser gets cut off depends on the width.
                CanExpand = s.Description.Length > 0,
            };
        }

        protected override void OnExpandedChanged()
        {
            Changed(nameof(ShowPreview));
            Changed(nameof(ShowDescription));
            Changed(nameof(ShowUntranslated));
        }
    }

    /// <summary>One rung of the C1-C6 / E1-E6 / M1-M6 ladder.</summary>
    public sealed class ProgressionRow : ExpandableRow
    {
        public ProgressionRow(string numeral, string name, string description)
        {
            Numeral = numeral;
            Name = name ?? "";
            Title = Name;
            Subtitle = "";
            Description = description ?? "";
            Preview = CharacterOverviewViewModel.Flatten(Description);
        }

        internal ProgressionRow(string numeral, OverviewSkill skill, bool isFirst, bool isLast)
            : this(numeral, skill.Name, skill.Description)
        {
            (Title, Subtitle) = CharacterOverviewViewModel.Names(skill.EnglishName, skill.Name);
            IconPath = skill.IconPath;
            IsUntranslated = skill.DescriptionInEnglish;
            IsFirst = isFirst;
            IsLast = isLast;
        }

        public string Numeral { get; }
        public string Name { get; }
        public bool IsUntranslated { get; }
        /// <summary>The preview is the description itself, so the marker always applies.</summary>
        public bool ShowUntranslated => IsUntranslated;
        public string Title { get; }
        public string Subtitle { get; }
        public string IconPath { get; }
        public bool IsFirst { get; }
        public bool IsLast { get; }
        public string Preview { get; }
        public string Description { get; }
        public bool CanExpand => Description.Length > 0;
        public bool ShowPreview => !IsExpanded && Preview.Length > 0;

        protected override void OnExpandedChanged() => Changed(nameof(ShowPreview));
    }

    public sealed class SetGroupRow
    {
        public string PiecesText { get; private set; } = "";
        public IReadOnlyList<SetOptionRow> Options { get; private set; } = Array.Empty<SetOptionRow>();

        internal static SetGroupRow From(OverviewSetGroup g) => new SetGroupRow
        {
            PiecesText = g.Combine ? string.Join("+", g.Options.Select(_ => g.Pieces)) : g.Pieces.ToString(),
            Options = g.Options.Select((o, i) => new SetOptionRow(o, i, g.Combine, g.Pieces)).ToList(),
        };
    }

    public sealed class SetOptionRow : ExpandableRow
    {
        internal SetOptionRow(OverviewSet set, int index, bool combine, int pieces = 0)
        {
            // "4 × A lub B" or "2 × A + 2 × B": the count before the first chip, before each chip of a pair.
            PiecesLabel = pieces > 0 && (index == 0 || combine) ? $"{pieces} ×" : "";
            Name = string.IsNullOrWhiteSpace(set.EnglishName) ? set.Name : set.EnglishName;
            Gloss = CharacterOverviewViewModel.GlossOf(new[] { Name }, new[] { set.Name }, "");
            IconPath = set.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(Name);
            ShowOr = index > 0 && !combine;
            ShowPlus = index > 0 && combine;
            Bonuses = set.BonusItems.Count > 0
                ? set.BonusItems.Select(b => BonusLine.From(b.Pieces, b.Text)).ToList()
                : set.Bonuses.Select(BonusLine.Parse).ToList();
            BonusesText = string.Join(Environment.NewLine, (Gloss.Length > 0 ? new[] { Gloss } : Array.Empty<string>()).Concat(set.Bonuses));
        }

        /// <summary>The name the game shows (English).</summary>
        public string Name { get; }
        /// <summary>The translated name, "" when it reads the same.</summary>
        public string Gloss { get; }
        public bool HasGloss => Gloss.Length > 0;
        /// <summary>"4 ×" before the chip it applies to, else "".</summary>
        public string PiecesLabel { get; }
        public bool HasPieces => PiecesLabel.Length > 0;
        public string IconPath { get; }
        public string Initial { get; }
        public bool ShowOr { get; }
        public bool ShowPlus { get; }
        public IReadOnlyList<BonusLine> Bonuses { get; }
        public string BonusesText { get; }
    }

    public sealed class BonusLine
    {
        public string Pieces { get; private set; } = "";
        public string Text { get; private set; } = "";

        internal static BonusLine From(int pieces, string text) =>
            new BonusLine { Pieces = pieces > 0 ? pieces.ToString() : "", Text = text ?? "" };

        /// <summary>"4: text" (as built by CharacterOverviewBuilder) → 4 + text.</summary>
        internal static BonusLine Parse(string line)
        {
            line = line ?? "";
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            bool numbered = colon > 0 && colon <= 2 && line.Take(colon).All(char.IsDigit);
            return numbered
                ? new BonusLine { Pieces = line.Substring(0, colon), Text = line.Substring(colon + 2) }
                : new BonusLine { Text = line };
        }
    }

    /// <summary>One priority checklist line; the weapon label follows the game's word for weapons.</summary>
    public sealed class TipRow
    {
        internal TipRow(OverviewHighlight h, string game)
        {
            LabelKey = h.LabelKey == "Char_Tip_Weapon" && game.Length > 0 ? "Char_Tip_Weapon_" + game : h.LabelKey;
            Text = h.Text;
            Gloss = h.Gloss ?? "";
            IsAi = h.IsAi;
            IconPath = h.IconPath;
            HasIconSlot = h.IconUrl != null;
            Initial = CharacterOverviewViewModel.InitialOf(h.Text);
        }

        public string LabelKey { get; }
        public string Text { get; }
        /// <summary>The terms in the pack's language, shown smaller beneath; "" when they read the same.</summary>
        public string Gloss { get; }
        public bool HasGloss => Gloss.Length > 0;
        public bool IsAi { get; }
        public bool ShowAiTag { get; internal set; }
        public string IconPath { get; }
        /// <summary>Sets and weapons have an image slot; stat and leveling lines do not.</summary>
        public bool HasIconSlot { get; }
        public string Initial { get; }
    }

    public sealed class MaterialRow
    {
        internal MaterialRow(OverviewMaterial m)
        {
            Name = string.IsNullOrWhiteSpace(m.EnglishName) ? m.Name : m.EnglishName;
            Gloss = CharacterOverviewViewModel.GlossOf(new[] { Name }, new[] { m.Name }, "");
            CountText = "×" + m.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            IconPath = m.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(Name);
        }

        /// <summary>The name the game shows (English); the translation is the tooltip.</summary>
        public string Name { get; }
        public string Gloss { get; }
        public bool HasGloss => Gloss.Length > 0;
        public string CountText { get; }
        public string IconPath { get; }
        public string Initial { get; }
    }

    public sealed class LinkRow
    {
        internal LinkRow(string label, string url, string iconKey = LinkHosts.Generic, string monogram = "")
        {
            Label = label ?? "";
            Url = url ?? "";
            IconKey = iconKey ?? LinkHosts.Generic;
            Monogram = monogram ?? "";
        }

        public string Label { get; }
        public string Url { get; }
        /// <summary>The site's mark, drawn by the app: a letter tile, the video glyph, else a link glyph.</summary>
        public string IconKey { get; }
        public string Monogram { get; }
        public bool HasMonogram => Monogram.Length > 0;
        public bool IsVideo => IconKey == LinkHosts.YouTube;
        public bool IsGeneric => !HasMonogram && !IsVideo;
    }

    /// <summary>A guide section that opens in place; the first one starts open.</summary>
    public sealed class GuideRow : ExpandableRow
    {
        internal GuideRow(string heading, string body, bool expanded)
        {
            Heading = heading ?? "";
            Body = body ?? "";
            Blocks = GuideBlock.Parse(Body);
            IsExpanded = expanded;
        }

        public string Heading { get; }
        public string Body { get; }
        public IReadOnlyList<GuideBlock> Blocks { get; }
    }

    /// <summary>A team comp card: members with roles, why it works, and where it comes from.</summary>
    public sealed class TeamCardRow
    {
        internal TeamCardRow(OverviewTeam team, RotationWords words = null)
        {
            Name = team.Name;
            Why = team.Why;
            Source = team.Source ?? "";
            IsOfficial = Source == "official";
            IsAi = CharacterTeamIndex.IsUpdaterPick(Source); // "ai", "ai+sources", "ai+curated": no source label
            Members = team.Members.Select(m => new TeamMemberRow(m)).ToList();
            Rotations = (team.Rotations ?? Array.Empty<OverviewRotation>()).Where(r => r?.Steps?.Count > 0).Select(r => new RotationRow(r, words)).ToList();
        }

        public string Name { get; }
        public string Why { get; }
        /// <summary>The provider's label as written in the data, for sources the app does not know.</summary>
        public string Source { get; }
        public bool IsOfficial { get; }
        public bool IsAi { get; }
        public bool IsOtherSource => !IsOfficial && !IsAi;
        public IReadOnlyList<TeamMemberRow> Members { get; }
        /// <summary>The rotations a guide states for the team, under the why; empty when it has none.</summary>
        public IReadOnlyList<RotationRow> Rotations { get; }
        public bool HasRotations => Rotations.Count > 0;
    }

    /// <summary>A rotation on a team card: the members' turns as one wrapping strip, the named combos as footnotes.</summary>
    public sealed class RotationRow
    {
        internal RotationRow(OverviewRotation rotation, RotationWords words = null)
        {
            Variant = rotation.Variant ?? "";
            VariantTakesZe = RotationNotation.TakesPolishZe(Variant);
            Steps = rotation.Steps.Select((s, i) => new RotationStepRow(s, showSeparator: i > 0, words)).ToList();
            // "* text", "** text": the marker on the combo's chip, then the combo as the page spells it.
            Footnotes = (rotation.Combos ?? Array.Empty<OverviewRotationCombo>()).Select(c => c.Marker + " " + c.Text).ToList();
            Text = string.Join(" > ", Steps.Select(s => s.Text));
        }

        /// <summary>The member this rotation is for, "with Prune" / "z Prune"; "" when the page offers no alternative.</summary>
        public string Variant { get; }
        public bool HasVariant => Variant.Length > 0;
        /// <summary>Polish reads "ze" before this name ("ze Skirk"); the English strings say "with" either way.</summary>
        public bool VariantTakesZe { get; }
        public IReadOnlyList<RotationStepRow> Steps { get; }
        public IReadOnlyList<string> Footnotes { get; }
        public bool HasFootnotes => Footnotes.Count > 0;
        /// <summary>"Durin: E ×2, Q > Prune: E, Q": the strip as plain text (its accessible name).</summary>
        public string Text { get; }
    }

    /// <summary>One member's turn: a "&gt;" before every turn but the first, the portrait, the chips, then the page's condition.</summary>
    public sealed class RotationStepRow
    {
        internal RotationStepRow(OverviewRotationStep step, bool showSeparator, RotationWords words = null)
        {
            Name = step.MemberName ?? "";
            IconPath = step.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(Name);
            IsSelf = step.IsSelf;
            ShowName = step.IconUrl == null && step.IconPath == null;
            ShowSeparator = showSeparator;
            Chips = (step.Actions ?? Array.Empty<OverviewRotationAction>()).Select(a => new RotationChipRow(a, words)).ToList();
            Note = step.Note ?? "";
            Text = Name + ": " + string.Join(", ", Chips.Select(c => c.Text)) + (Note.Length > 0 ? " (" + Note + ")" : "");
        }

        /// <summary>English, like every character name.</summary>
        public string Name { get; }
        public string IconPath { get; }
        public string Initial { get; }
        /// <summary>The page's own character: its face is ringed in the element colour.</summary>
        public bool IsSelf { get; }
        /// <summary>The bundle has no portrait for this member: the name follows the initial.</summary>
        public bool ShowName { get; }
        public bool ShowSeparator { get; }
        public IReadOnlyList<RotationChipRow> Chips { get; }
        /// <summary>The page's condition on this turn, English as written ("Omit if C6"), a quiet line under the chips.</summary>
        public string Note { get; }
        public bool HasNote => Note.Length > 0;
        /// <summary>"Durin: E ×2, Q", then the condition in brackets.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// One input (<see cref="RotationNotation"/>): E, Q and N as the member's skill icon on a disc (a choice of two: a pill), "×2"
    /// top-right and a hold's "H" bottom-right; any other input a text chip ("N2", "CA ×2", "E hold", "E / Q", "Combo*"). An
    /// optional input has a dashed outline.
    /// </summary>
    public sealed class RotationChipRow
    {
        internal RotationChipRow(OverviewRotationAction a, RotationWords words = null)
        {
            string alt = a.Alt ?? "";
            bool hasIcon = a.IconUrl != null || a.IconPath != null;
            bool altHasIcon = a.AltIconUrl != null || a.AltIconPath != null;
            IsChoice = alt.Length > 0;
            IsHeld = a.Hold;
            IsOptional = a.Optional;
            IsIcon = RotationNotation.HasIconSlot(a.Action, hasIcon, alt, altHasIcon);
            Label = RotationNotation.Label(a.Action, a.Count, IsIcon, alt);
            Letter = RotationNotation.Label(a.Action, 1, hasIcon: true);
            AltLetter = IsChoice ? RotationNotation.Label(alt, 1, hasIcon: true) : "";
            Count = RotationNotation.Count(a.Action, a.Count, IsIcon, alt);
            Marker = IsIcon ? "" : a.Marker ?? "";
            IconPath = IsIcon ? a.IconPath : null;
            AltIconPath = IsIcon && IsChoice ? a.AltIconPath : null;
            Text = RotationNotation.Text(a.Action, a.Count, IsIcon, Marker, IsHeld, alt, IsOptional);
            // The builder's English name, with the flag words in the interface's language ("(opcjonalnie)").
            Description = !string.IsNullOrEmpty(a.Name)
                ? RotationNotation.Describe(a.Name, a.AltName, a.Count, a.Marker, RotationNotation.Flags(IsChoice, IsHeld, IsOptional, words))
                : string.IsNullOrEmpty(a.Description) ? Text : a.Description;
        }

        /// <summary>The disc's letter until its icon is on disk, or the text chip's text: "E", "N", "N2", "CA", "Combo", "E / Q".</summary>
        public string Label { get; }
        /// <summary>A pair's letters until their icons are on disk: "E" and "Q".</summary>
        public string Letter { get; }
        public string AltLetter { get; }
        /// <summary>"×2" for a repeat; "" for one, and next to an "N2".</summary>
        public string Count { get; }
        /// <summary>The badge on an icon chip's corner.</summary>
        public bool HasBadge => IsIcon && Count.Length > 0;
        /// <summary>" ×2" after a text chip's label, "" when it has no count.</summary>
        public string InlineCount => !IsIcon && Count.Length > 0 ? " " + Count : "";
        /// <summary>"*", "**" on a combo whose text the footnotes spell out.</summary>
        public string Marker { get; }
        /// <summary>E, Q or N drawn from the member's own skill icon (a choice: both icons).</summary>
        public bool IsIcon { get; }
        public bool IsText => !IsIcon;
        /// <summary>One icon on a disc, or a choice's two icons in one pill.</summary>
        public bool IsDisc => IsIcon && !IsChoice;
        public bool IsPair => IsIcon && IsChoice;
        public string IconPath { get; }
        public string AltIconPath { get; }
        /// <summary>A choice between two inputs ("E / Q").</summary>
        public bool IsChoice { get; }
        /// <summary>A held skill: the "H" tag on an icon chip, " hold" after a text chip's label.</summary>
        public bool IsHeld { get; }
        public bool HasHoldTag => IsIcon && IsHeld;
        public string HoldTag => RotationNotation.HoldTag;
        public string InlineHold => !IsIcon && IsHeld ? " " + RotationNotation.HoldWord : "";
        /// <summary>The page marks the input optional: a dashed outline.</summary>
        public bool IsOptional { get; }
        /// <summary>The chip as plain text: "E ×2", "CA ×2", "N2", "Combo*", "E hold", "E / Q", "(Q)".</summary>
        public string Text { get; }
        /// <summary>Tooltip and accessible name, as the site's: "Elemental Skill: Passion Overload ×2 (hold)".</summary>
        public string Description { get; }
    }

    public sealed class TeamMemberRow
    {
        internal TeamMemberRow(OverviewTeamMember m)
        {
            Name = m.Name;
            Role = m.Role;
            IconPath = m.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(m.Name);
            RankLabel = m.RankLabel ?? "";
        }

        public string Name { get; }
        public string Role { get; }
        public string IconPath { get; }
        public string Initial { get; }
        /// <summary>The rank the team assumes ("C1", "E0", "M2"), a badge after the name; "" for none.</summary>
        public string RankLabel { get; }
        public bool HasRank => RankLabel.Length > 0;
    }

    /// <summary>One row of "Gra też w drużynach": the host, the host's team and this character's role in it.</summary>
    public sealed class TeamHostRow
    {
        internal TeamHostRow(OverviewTeamHost host)
        {
            HostId = host.HostId;
            HostName = host.HostName;
            IconPath = host.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(host.HostName);
            TeamName = host.TeamName;
            Role = host.Role;
            RankLabel = host.RankLabel ?? "";
        }

        public string HostId { get; }
        public string HostName { get; }
        public string IconPath { get; }
        public string Initial { get; }
        public string TeamName { get; }
        public string Role { get; }
        /// <summary>The rank the host's team assumes for this character ("C1"), a badge before the role; "" for none.</summary>
        public string RankLabel { get; }
        public bool HasRank => RankLabel.Length > 0;
        public bool HasRoleLine => HasRank || !string.IsNullOrEmpty(Role);
        /// <summary>What a screen reader says for the card: the host, and the rank its team assumes for this character.</summary>
        public string AutomationName => HasRank ? $"{HostName}, {RankLabel}" : HostName;
    }

    /// <summary>One row of the goal table: the stat as the game names it, the value to aim for, a quiet note under it.</summary>
    public sealed class StatGoalRow
    {
        private StatGoalRow(OverviewStatGoal goal, string value, string language, bool isFirst)
        {
            Stat = goal.Stat ?? "";
            Value = value;
            Note = goal.Note?.PickFor(language) ?? "";
            IsFirst = isFirst;
        }

        /// <summary>English, as the game names it.</summary>
        public string Stat { get; }
        /// <summary>"2,500–3,000+" / "2 500–3 000+", "200%+", "100–130%".</summary>
        public string Value { get; }
        public string Note { get; }
        public bool HasNote => Note.Length > 0;
        /// <summary>The first row has no rule above it.</summary>
        public bool IsFirst { get; }
        public string AutomationName => HasNote ? $"{Stat}: {Value}. {Note}" : $"{Stat}: {Value}";

        /// <summary>The rows in the data's order; a goal with nothing to write is left out.</summary>
        internal static IReadOnlyList<StatGoalRow> For(IReadOnlyList<OverviewStatGoal> goals, string language)
        {
            var rows = new List<StatGoalRow>();
            foreach (var g in goals ?? Array.Empty<OverviewStatGoal>())
            {
                string value = g?.ValueFor(language) ?? "";
                if (value.Length == 0 || string.IsNullOrWhiteSpace(g.Stat)) continue;
                rows.Add(new StatGoalRow(g, value, language, rows.Count == 0));
            }
            return rows;
        }
    }

    /// <summary>A teammate chip: portrait when cached, otherwise the initial.</summary>
    public sealed class TeammateChip
    {
        internal TeammateChip(string name, string iconPath)
        {
            Name = name ?? "";
            IconPath = iconPath;
            Initial = CharacterOverviewViewModel.InitialOf(Name);
        }

        public string Name { get; }
        public string IconPath { get; }
        public string Initial { get; }
    }

    public sealed class MainStatRow
    {
        public string SlotKey { get; set; } = "";
        public Geometry Icon { get; set; }
        /// <summary>Stat names as the game shows them (English).</summary>
        public string Primary { get; set; } = "";
        public string Alternatives { get; set; } = "";
        /// <summary>The same stats translated, beneath; "" when they read the same.</summary>
        public string Gloss { get; set; } = "";
        public bool HasGloss => Gloss.Length > 0;
    }

    /// <summary>One substat in priority order: the order carries the rank, the first chip is marked.</summary>
    public sealed class RankedStat
    {
        public int Rank { get; set; }
        /// <summary>The stat as the game shows it (English); the translation, when it says something, beneath.</summary>
        public string Name { get; set; } = "";
        public string Gloss { get; set; } = "";
        public bool HasGloss => Gloss.Length > 0;
        public bool IsFirst => Rank == 1;
        /// <summary>A muted "›" before every chip but the first.</summary>
        public bool ShowSeparator => Rank > 1;
        /// <summary>What a screen reader says: the rank the chip no longer prints, the stat, its translation.</summary>
        public string AutomationName => HasGloss ? $"{Rank}. {Name} ({Gloss})" : $"{Rank}. {Name}";
    }

    public sealed class WeaponRow : ExpandableRow
    {
        internal WeaponRow(OverviewWeapon w, string game)
        {
            RarityText = w.RarityText;
            RarityTier = CharacterOverviewViewModel.Tier(game, w.Rarity);
            IsRank = RarityText.Length == 1 && char.IsLetter(RarityText[0]);
            IconPath = w.IconPath;
            Name = string.IsNullOrWhiteSpace(w.EnglishName) ? w.Name : w.EnglishName;
            Gloss = CharacterOverviewViewModel.GlossOf(new[] { Name }, new[] { w.Name }, "");
            Initial = CharacterOverviewViewModel.InitialOf(Name);
            PassiveName = string.IsNullOrWhiteSpace(w.EnglishPassiveName) ? w.PassiveName : w.EnglishPassiveName;
            PassiveGloss = CharacterOverviewViewModel.GlossOf(new[] { PassiveName }, new[] { w.PassiveName }, "");
            Passive = w.Passive;
        }

        /// <summary>The name the game shows (English).</summary>
        public string Name { get; }
        /// <summary>" (translated name)" after it, "" when it reads the same.</summary>
        public string Gloss { get; }
        public string GlossSuffix => Gloss.Length > 0 ? " (" + Gloss + ")" : "";
        public string PassiveGloss { get; }
        public bool HasPassiveGloss => PassiveGloss.Length > 0;
        public string RarityText { get; }
        public string RarityTier { get; }
        public bool IsRank { get; }
        public bool ShowStars => RarityText.Length > 0 && !IsRank;
        public string IconPath { get; }
        public string Initial { get; }
        public string PassiveName { get; }
        public string Passive { get; }
        public bool CanExpand => Passive.Length > 0;
        public bool ShowPassive => IsExpanded && Passive.Length > 0;

        protected override void OnExpandedChanged() => Changed(nameof(ShowPassive));
    }
}
