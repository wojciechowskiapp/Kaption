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

        private readonly Color _hue;
        private ImageSource _heroArt;
        private ImageSource _ambience;
        private bool _darkSurface;

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
            MainStats = model.MainStats.Where(m => m.Stats.Count > 0).Select(m => new MainStatRow
            {
                SlotKey = m.SlotKey,
                Icon = SlotIcons.For(m.Slot),
                Primary = m.Stats[0],
                Alternatives = m.Stats.Count > 1 ? " / " + string.Join(" / ", m.Stats.Skip(1)) : "",
            }).ToList();
            SubStats = model.SubStats.Select((s, i) => new RankedStat { Rank = i + 1, Name = s }).ToList();
            Weapons = model.Weapons.Select(w => new WeaponRow(w, Game)).ToList();

            // When the whole build is an AI pick the section says so once; otherwise each AI line does.
            Tips = model.Highlights.Select(h => new TipRow(h, Game) { ShowAiTag = h.IsAi && !(model.HasBuild && model.BuildIsAi) }).ToList();
            MaterialsAscension = model.MaterialsAscension.Select(m => new MaterialRow(m)).ToList();
            MaterialsSkills = model.MaterialsSkills.Select(m => new MaterialRow(m)).ToList();
            Links = model.Links.Select(l => new LinkRow(l.Label, l.Url) { IconPath = l.IconPath }).ToList();

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
            Teams = model.Teams.Select(t => new TeamCardRow(t)).ToList();
            TeammateCards = model.TeammateCards.Count > 0
                ? model.TeammateCards.Select(t => new TeammateChip(t.Name, t.IconPath)).ToList()
                : model.Teammates.Select(n => new TeammateChip(n, null)).ToList();
            Sections = BuildSections();

            _heroArt = HeroArtCached(model);
            if (_heroArt == null && (model.SplashPath != null || model.IconPath != null))
                Later(() => LoadHeroArt(model), a => { _heroArt = a; Changed(nameof(HeroArt)); Changed(nameof(HasSplash)); Changed(nameof(HasPortraitArt)); Changed(nameof(HasNoArt)); });
            if (HasAmbience)
            {
                _ambience = IconImages.CachedOrNull(model.AmbiencePath, OverviewImageSizes.Ambience);
                if (_ambience == null) Later(() => IconImages.Load(model.AmbiencePath, OverviewImageSizes.Ambience), a => Ambience = a);
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
            IconImages.Load(model.AmbiencePath, OverviewImageSizes.Ambience);
            foreach (var s in model.Skills)
                IconImages.Load(s.IconPath, ProgressionLetters.ContainsKey(s.Kind ?? "") ? OverviewImageSizes.Rank : OverviewImageSizes.Skill);
            foreach (var o in model.SetGroups.SelectMany(g => g.Options)) IconImages.Load(o.IconPath, OverviewImageSizes.Set);
            foreach (var w in model.Weapons) IconImages.Load(w.IconPath, OverviewImageSizes.Weapon);
            foreach (var m in model.Teams.SelectMany(t => t.Members)) IconImages.Load(m.IconPath, OverviewImageSizes.Member);
            foreach (var m in model.TeammateCards) IconImages.Load(m.IconPath, OverviewImageSizes.Member);
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
        public bool HasSummaryOrArt => HasSummary || HasAmbience;
        public bool HasAmbience => !string.IsNullOrEmpty(Model.AmbiencePath);
        public ImageSource Ambience
        {
            get => _ambience;
            private set { if (!ReferenceEquals(_ambience, value)) { _ambience = value; Changed(); } }
        }
        public string AmbienceAlt => Model.AmbienceAlt;
        public bool HasAmbienceAlt => !string.IsNullOrWhiteSpace(Model.AmbienceAlt);
        /// <summary>Reserved before the image decodes, so nothing jumps.</summary>
        public double AmbienceHeight => OverviewImageSizes.Ambience / (Model.AmbienceAspect > 0.3 ? Model.AmbienceAspect : 16.0 / 9.0);
        public IReadOnlyList<FactTile> Facts { get; }
        public bool HasGlance => HasSummary || HasAmbience || Facts.Count > 0;

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
        public bool HasBuildSection => HasBuild || Tips.Count > 0;

        // ── teams ──
        public IReadOnlyList<TeamCardRow> Teams { get; }
        public bool HasTeamCards => Teams.Count > 0;
        public IReadOnlyList<string> Teammates => Model.Teammates;
        public IReadOnlyList<TeammateChip> TeammateCards { get; }
        /// <summary>The game's own teammates, shown only when there are no team comps.</summary>
        public bool ShowTeammates => Teams.Count == 0 && TeammateCards.Count > 0;
        public bool HasTeams => Teams.Count > 0 || TeammateCards.Count > 0;

        // ── tips, materials, guide, links ──
        public IReadOnlyList<TipRow> Tips { get; }
        public IReadOnlyList<MaterialRow> MaterialsAscension { get; }
        public IReadOnlyList<MaterialRow> MaterialsSkills { get; }
        public IReadOnlyList<LinkRow> Links { get; }
        public IReadOnlyList<GuideRow> Guide { get; }
        public bool HasTips => Model.HasTips;
        public bool HasGuide => Guide.Count > 0;
        public bool HasMaterials => MaterialsAscension.Count > 0 || MaterialsSkills.Count > 0;
        public string PageUrl => Model.PageUrl;
        public bool HasPageUrl => Model.PageUrl.Length > 0;
        public bool HasLinks => HasPageUrl || Links.Count > 0;
        public string WeaponsKey => Game.Length > 0 ? "Char_Weapons_" + Game : "Char_Weapons";

        private IReadOnlyList<SectionLink> BuildSections()
        {
            var list = new List<SectionLink>();
            void Add(bool shown, string key, string labelKey) { if (shown) list.Add(new SectionLink(key, labelKey)); }
            Add(HasGlance, "Glance", "Char_Nav_Glance");
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
                facts.Add(new FactTile("Char_Fact_Signature", signature.Title, signature));
            if (model.Leveling.Count > 0)
            {
                var first = skills.FirstOrDefault(s => s.Title == model.Leveling[0]);
                facts.Add(new FactTile("Char_Fact_LevelFirst", model.Leveling[0], first) { IsAi = model.LevelingIsAi });
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
            Options = g.Options.Select((o, i) => new SetOptionRow(o, i, g.Combine)).ToList(),
        };
    }

    public sealed class SetOptionRow : ExpandableRow
    {
        internal SetOptionRow(OverviewSet set, int index, bool combine)
        {
            Name = set.Name;
            IconPath = set.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(set.Name);
            ShowOr = index > 0 && !combine;
            ShowPlus = index > 0 && combine;
            Bonuses = set.BonusItems.Count > 0
                ? set.BonusItems.Select(b => BonusLine.From(b.Pieces, b.Text)).ToList()
                : set.Bonuses.Select(BonusLine.Parse).ToList();
            BonusesText = string.Join(Environment.NewLine, set.Bonuses);
        }

        public string Name { get; }
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
            IsAi = h.IsAi;
            IconPath = h.IconPath;
            HasIconSlot = h.IconUrl != null;
            Initial = CharacterOverviewViewModel.InitialOf(h.Text);
        }

        public string LabelKey { get; }
        public string Text { get; }
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
            Name = m.Name;
            CountText = "×" + m.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            IconPath = m.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(m.Name);
        }

        public string Name { get; }
        public string CountText { get; }
        public string IconPath { get; }
        public string Initial { get; }
    }

    public sealed class LinkRow
    {
        internal LinkRow(string label, string url)
        {
            Label = label ?? "";
            Url = url ?? "";
        }

        public string Label { get; }
        public string Url { get; }
        /// <summary>The site's favicon on disk; null shows a globe.</summary>
        public string IconPath { get; internal set; }
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
        internal TeamCardRow(OverviewTeam team)
        {
            Name = team.Name;
            Why = team.Why;
            Source = team.Source ?? "";
            IsOfficial = Source == "official";
            IsAi = Source == "ai";
            Members = team.Members.Select(m => new TeamMemberRow(m)).ToList();
        }

        public string Name { get; }
        public string Why { get; }
        /// <summary>The provider's label as written in the data, for sources the app does not know.</summary>
        public string Source { get; }
        public bool IsOfficial { get; }
        public bool IsAi { get; }
        public bool IsOtherSource => !IsOfficial && !IsAi;
        public IReadOnlyList<TeamMemberRow> Members { get; }
    }

    public sealed class TeamMemberRow
    {
        internal TeamMemberRow(OverviewTeamMember m)
        {
            Name = m.Name;
            Role = m.Role;
            IconPath = m.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(m.Name);
        }

        public string Name { get; }
        public string Role { get; }
        public string IconPath { get; }
        public string Initial { get; }
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
        public string Primary { get; set; } = "";
        public string Alternatives { get; set; } = "";
    }

    public sealed class RankedStat
    {
        public int Rank { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class WeaponRow : ExpandableRow
    {
        internal WeaponRow(OverviewWeapon w, string game)
        {
            Name = w.Name;
            RarityText = w.RarityText;
            RarityTier = CharacterOverviewViewModel.Tier(game, w.Rarity);
            IsRank = RarityText.Length == 1 && char.IsLetter(RarityText[0]);
            IconPath = w.IconPath;
            Initial = CharacterOverviewViewModel.InitialOf(w.Name);
            PassiveName = w.PassiveName;
            Passive = w.Passive;
        }

        public string Name { get; }
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
