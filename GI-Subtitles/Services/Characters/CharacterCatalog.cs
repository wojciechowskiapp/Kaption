using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Characters
{
    public enum IconKind
    {
        Character,
        Weapon,
        Set,
        Material,
        /// <summary>Full-body art (Phase D); absent for a game without a verified source.</summary>
        Splash,
        Skill,
    }

    public sealed class MaterialCost
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public string Icon { get; set; } = "";
    }

    /// <summary>Totals to max level (ascension) and all skills to max (skills).</summary>
    public sealed class CharacterMaterials
    {
        public IReadOnlyList<MaterialCost> Ascension { get; set; } = Array.Empty<MaterialCost>();
        public IReadOnlyList<MaterialCost> Skills { get; set; } = Array.Empty<MaterialCost>();
    }

    /// <summary>A guide page elsewhere, verified by the extractor at build time. https only.</summary>
    public sealed class ExternalLink
    {
        public string Site { get; set; } = "";
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>AI text in two languages; either may be empty. A plain string in the data counts as English.</summary>
    public sealed class AiLocalized
    {
        public static readonly AiLocalized Empty = new AiLocalized();
        public string En { get; set; } = "";
        public string Pl { get; set; } = "";
        public bool IsEmpty => En.Length == 0 && Pl.Length == 0;

        /// <summary>Polish first (the shipped target language), English as the fallback.</summary>
        public string Pick() => Pl.Length > 0 ? Pl : En;

        /// <summary>The interface's language first ("pl" picks Polish, anything else English), the other as the fallback.</summary>
        public string PickFor(string language) =>
            CharacterPageLink.IsPolish(language) ? Pick() : En.Length > 0 ? En : Pl;
    }

    /// <summary>An ambience image in the element's mood (no likeness, no text), made by the updater. https only.</summary>
    public sealed class AiImage
    {
        public string Url { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public AiLocalized Alt { get; set; } = AiLocalized.Empty;
        /// <summary>Where a crop keeps the picture's subject ("focal": {x, y}, each 0 to 1); null when the data has none.</summary>
        public double? FocalX { get; set; }
        public double? FocalY { get; set; }
    }

    /// <summary>A team comp. <see cref="Source"/> is an open label: official, ai, or a future provider.</summary>
    public sealed class AiTeam
    {
        public AiLocalized Name { get; set; } = AiLocalized.Empty;
        public string Source { get; set; } = "";
        public IReadOnlyList<string> Members { get; set; } = Array.Empty<string>();
        /// <summary>Role per member id.</summary>
        public IReadOnlyDictionary<string, AiLocalized> Roles { get; set; } = new Dictionary<string, AiLocalized>();
        public AiLocalized Why { get; set; } = AiLocalized.Empty;
        /// <summary>Member id → the constellation / eidolon / mindscape (0..6) the team assumes; only members it matters for.</summary>
        public IReadOnlyDictionary<string, int> Ranks { get; set; } = new Dictionary<string, int>();
        /// <summary>The rotations a guide states for this team (one or two), in the data's order; empty when none does.</summary>
        public IReadOnlyList<TeamRotation> Rotations { get; set; } = Array.Empty<TeamRotation>();
    }

    /// <summary>One action of a rotation step (<see cref="RotationNotation"/>), done <see cref="Count"/> times.</summary>
    public sealed class RotationAction
    {
        /// <summary>"E", "Q", "N", "CA", "P", "D" or "combo".</summary>
        public string Action { get; set; } = "";
        /// <summary>1 when the data gives no count; for N the number of hits.</summary>
        public int Count { get; set; } = 1;
        /// <summary>A held skill (KQM's "hE"); a tap is the plain action.</summary>
        public bool Hold { get; set; }
        /// <summary>The page marks the action optional (KQM's "(Q)").</summary>
        public bool Optional { get; set; }
        /// <summary>The other input of a choice: KQM's "E/Q" is Action "E", Alt "Q"; "" when there is no choice.</summary>
        public string Alt { get; set; } = "";
    }

    /// <summary>One member's turn in a rotation.</summary>
    public sealed class RotationStep
    {
        /// <summary>A member id of the team.</summary>
        public string Member { get; set; } = "";
        public IReadOnlyList<RotationAction> Actions { get; set; } = Array.Empty<RotationAction>();
        /// <summary>The page's condition on this turn, English ("Omit if C6"); "" for none.</summary>
        public string Note { get; set; } = "";
    }

    /// <summary>A rotation as a guide page states it for the team (the updater transcribes, never composes one).</summary>
    public sealed class TeamRotation
    {
        /// <summary>The English name of the member this rotation is for when the page offers alternatives ("Prune"), else "".</summary>
        public string Variant { get; set; } = "";
        public IReadOnlyList<RotationStep> Steps { get; set; } = Array.Empty<RotationStep>();
        /// <summary>Member id → that member's named combo as the page spells it (English).</summary>
        public IReadOnlyDictionary<string, string> Combo { get; set; } = new Dictionary<string, string>();
        /// <summary>Every member has a turn (true) or someone sits out (false); null when the data does not say. Not shown.</summary>
        public bool? Covers { get; set; }
    }

    /// <summary>
    /// A stat value to aim for ("CRIT Rate 60–70%", "Energy Recharge 200%+"). At least one bound is set, and
    /// min ≤ max when both are.
    /// </summary>
    public sealed class StatGoal
    {
        /// <summary>A key of the section's stat catalog (<see cref="CharacterCatalog.Stats"/>), like a build's main stats.</summary>
        public string Stat { get; set; } = "";
        public double? Min { get; set; }
        public double? Max { get; set; }
        /// <summary>"%" or "".</summary>
        public string Unit { get; set; } = "";
        /// <summary>More is fine: the value gets a "+".</summary>
        public bool Open { get; set; }
        public AiLocalized Note { get; set; } = AiLocalized.Empty;
    }

    public sealed class GuideSection
    {
        public string Heading { get; set; } = "";
        public string Body { get; set; } = "";
    }

    /// <summary>Ranked picks from the AI pass; every id is validated upstream and again on read.</summary>
    public sealed class AiPriority
    {
        public IReadOnlyList<string> Sets { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Weapons { get; set; } = Array.Empty<string>();
        public IReadOnlyList<int> Skills { get; set; } = Array.Empty<int>();
        public IReadOnlyList<int> Progression { get; set; } = Array.Empty<int>();
        public bool IsEmpty => Sets.Count == 0 && Weapons.Count == 0 && Skills.Count == 0 && Progression.Count == 0;
    }

    /// <summary>English template plus HSR-style numeric params ("1[i]%" → p[0]).</summary>
    public sealed class GameText
    {
        public string Template { get; }
        public IReadOnlyList<double> Params { get; }

        public GameText(string template, IReadOnlyList<double> parameters)
        {
            Template = template ?? "";
            Params = parameters ?? Array.Empty<double>();
        }
    }

    public sealed class CharacterSkill
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>In-game icon name for <see cref="IconKind.Skill"/>, "" when the tables name none.</summary>
        public string Icon { get; set; } = "";
        /// <summary>"" = both kits; "original" / "enhanced" (HSR) / "potential" (ZZZ) = one switchable kit.</summary>
        public string Variant { get; set; } = "";
        public GameText Short { get; set; }
        public GameText Description { get; set; }
    }

    public sealed class BuildSetChoice
    {
        public int Pieces { get; set; }
        public IReadOnlyList<string> Ids { get; set; } = Array.Empty<string>();
        /// <summary>True: wear all ids together (Genshin 2+2). False: ranked alternatives.</summary>
        public bool Combine { get; set; }
    }

    public sealed class BuildMainStat
    {
        public string Slot { get; set; } = "";
        public IReadOnlyList<string> Stats { get; set; } = Array.Empty<string>();
    }

    public sealed class CharacterBuild
    {
        public IReadOnlyList<BuildSetChoice> Sets { get; set; } = Array.Empty<BuildSetChoice>();
        public IReadOnlyList<BuildMainStat> MainStats { get; set; } = Array.Empty<BuildMainStat>();
        public IReadOnlyList<string> SubStats { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Weapons { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Teammates { get; set; } = Array.Empty<string>();
    }

    /// <summary>AI-written text in one language. Skills are keyed by index into the record skills.</summary>
    public sealed class AiText
    {
        public IReadOnlyDictionary<int, string> Skills { get; set; } = new Dictionary<int, string>();
        public IReadOnlyList<string> Playstyle { get; set; } = Array.Empty<string>();
        public string BuildNote { get; set; } = "";
        /// <summary>The long article, in order; empty when the guide pass has not run.</summary>
        public IReadOnlyList<GuideSection> Guide { get; set; } = Array.Empty<GuideSection>();
        public bool IsEmpty => Skills.Count == 0 && Playstyle.Count == 0 && BuildNote.Length == 0 && Guide.Count == 0;
    }

    /// <summary>Optional, grounded AI layer (updater, gpt-6.1-terra). Never replaces official data.</summary>
    public sealed class CharacterAi
    {
        public string Model { get; set; } = "";
        public string GeneratedAt { get; set; } = "";
        public AiText En { get; set; }
        public AiText Pl { get; set; }
        public CharacterBuild Build { get; set; }
        public AiPriority Priority { get; set; }
        public AiImage Image { get; set; }
        public IReadOnlyList<AiTeam> Teams { get; set; } = Array.Empty<AiTeam>();
        /// <summary>Stat values to aim for, in the data's order; empty in bundles from before stat_goals.</summary>
        public IReadOnlyList<StatGoal> StatGoals { get; set; } = Array.Empty<StatGoal>();
    }

    public sealed class CharacterRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        /// <summary>Full-body art name for <see cref="IconKind.Splash"/>, "" when there is none.</summary>
        public string Splash { get; set; } = "";
        public int Rarity { get; set; }
        public string Element { get; set; } = "";
        public string WeaponType { get; set; } = "";
        public GameText Role { get; set; }
        public IReadOnlyList<CharacterSkill> Skills { get; set; } = Array.Empty<CharacterSkill>();
        public CharacterBuild Build { get; set; }
        public CharacterAi Ai { get; set; }
        /// <summary>The kit the game shows by default when the record has variants, else "".</summary>
        public string DefaultVariant { get; set; } = "";
        public CharacterMaterials Materials { get; set; }
        /// <summary>Official skill-upgrade priority as indices into Skills; empty when the game has none.</summary>
        public IReadOnlyList<int> Leveling { get; set; } = Array.Empty<int>();
        public IReadOnlyList<ExternalLink> Links { get; set; } = Array.Empty<ExternalLink>();
        /// <summary>The site's slug when the bundle carries one ("slug"), else "": then the app derives it.</summary>
        public string Slug { get; set; } = "";
        /// <summary>The character's kaption.one page when the bundle carries one ("page_url"), else "".</summary>
        public string PageUrl { get; set; } = "";
        /// <summary>Release day ("release_date", yyyy-MM-dd) when the bundle carries it, else "".</summary>
        public string ReleaseDate { get; set; } = "";
        /// <summary>The game version the character arrived in ("release_version", "5.4"), else "".</summary>
        public string ReleaseVersion { get; set; } = "";
        /// <summary>Distinct kit variants in order of appearance; empty for a plain record.</summary>
        public IReadOnlyList<string> Variants =>
            Skills.Select(s => s.Variant).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
    }

    public sealed class GearSetBonus
    {
        public int Pieces { get; set; }
        public GameText Text { get; set; }
    }

    public sealed class GearSet
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Kind { get; set; } = "";
        public IReadOnlyList<GearSetBonus> Bonuses { get; set; } = Array.Empty<GearSetBonus>();
    }

    public sealed class GameWeapon
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public int Rarity { get; set; }
        public string Type { get; set; } = "";
        public string PassiveName { get; set; } = "";
        public GameText Passive { get; set; }
    }

    /// <summary>
    /// Official per-character data from the gamedata bundle's `characters` section (schema 1),
    /// English only. Loading never throws: a bad record is skipped, a bad file yields Empty.
    /// </summary>
    public sealed class CharacterCatalog
    {
        public const int SupportedSchema = 1;
        public const long MaxBytes = 64L * 1024 * 1024;
        /// <summary>The updater writes 2 to 6 goals; entries past this are ignored.</summary>
        public const int MaxStatGoals = 6;
        /// <summary>The highest constellation / eidolon / mindscape.</summary>
        public const int MaxRank = 6;
        /// <summary>The updater writes one or two rotations per team; entries past this are ignored.</summary>
        public const int MaxRotations = 2;
        // Sanity caps on one rotation: past them the rotation is skipped, never cut short.
        public const int MaxRotationSteps = 24;
        public const int MaxStepActions = 12;
        public const int MaxActionCount = 20;
        public const int MaxVariantLength = 64;
        public const int MaxComboLength = 300;
        /// <summary>A step's note, the contract's cap: a longer one drops the rotation here and on the site.</summary>
        public const int MaxStepNoteLength = 80;

        public static readonly CharacterCatalog Empty = new CharacterCatalog(
            "", "", Array.Empty<CharacterRecord>(),
            new Dictionary<string, GearSet>(), new Dictionary<string, GameWeapon>(), new Dictionary<string, string>());

        private readonly Dictionary<string, CharacterRecord> _byId;
        private Dictionary<IconKind, (string Base, string Ext)> _icons = new Dictionary<IconKind, (string, string)>();
        private static readonly System.Text.RegularExpressions.Regex SafeIcon =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_\-./&]{1,160}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public string Game { get; }
        public string GameVersion { get; }
        public IReadOnlyList<CharacterRecord> Characters { get; }
        public IReadOnlyDictionary<string, GearSet> Sets { get; }
        public IReadOnlyDictionary<string, GameWeapon> Weapons { get; }
        public IReadOnlyDictionary<string, string> Stats { get; }
        /// <summary>The game's short name of a percent stat by key ("ATK%"), from the section's stats_short; may be empty.</summary>
        public IReadOnlyDictionary<string, string> StatsShort { get; private set; } = new Dictionary<string, string>();
        public bool IsEmpty => Characters.Count == 0;

        private CharacterCatalog(string game, string version, IReadOnlyList<CharacterRecord> characters,
            IReadOnlyDictionary<string, GearSet> sets, IReadOnlyDictionary<string, GameWeapon> weapons,
            IReadOnlyDictionary<string, string> stats)
        {
            Game = game;
            GameVersion = version;
            Characters = characters;
            Sets = sets;
            Weapons = weapons;
            Stats = stats;
            _byId = new Dictionary<string, CharacterRecord>(StringComparer.Ordinal);
            foreach (var c in characters)
                if (!_byId.ContainsKey(c.Id)) _byId[c.Id] = c;
        }

        public CharacterRecord Find(string id) =>
            id != null && _byId.TryGetValue(id, out var c) ? c : null;

        /// <summary>Public image URL for an icon name, or null (no source, unsafe name, no icon).</summary>
        public string IconUrl(IconKind kind, string icon)
        {
            if (string.IsNullOrEmpty(icon) || icon.Contains("..") || !SafeIcon.IsMatch(icon)) return null;
            return _icons.TryGetValue(kind, out var src) ? src.Base + icon + src.Ext : null;
        }

        /// <param name="expectedGame">Profile or wire id ("StarRail" / "starrail"); null skips the check.</param>
        public static CharacterCatalog Load(Stream stream, string expectedGame)
        {
            if (stream == null) return Empty;
            try
            {
                if (stream.CanSeek && stream.Length > MaxBytes)
                {
                    Logger.Log.Warn($"Character data is {stream.Length} bytes, over the {MaxBytes} cap; ignored.");
                    return Empty;
                }
                using (var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 }))
                    return FromRoot(doc.RootElement, expectedGame);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidOperationException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                Logger.Log.Warn($"Character data unreadable: {ex.Message}");
                return Empty;
            }
        }

        private static CharacterCatalog FromRoot(JsonElement root, string expectedGame)
        {
            if (root.ValueKind != JsonValueKind.Object) return Empty;
            if (Int(root, "schema") != SupportedSchema) return Empty;
            string game = Str(root, "game");
            if (!string.IsNullOrEmpty(expectedGame) && !string.Equals(game, expectedGame, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log.Warn($"Character data is for \"{game}\", expected \"{expectedGame}\"; ignored.");
                return Empty;
            }

            var stats = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Members(root, "stats"))
                if (p.Value.ValueKind == JsonValueKind.String) stats[p.Name] = p.Value.GetString();

            var sets = new Dictionary<string, GearSet>(StringComparer.Ordinal);
            foreach (var p in Members(root, "sets"))
            {
                var set = Safe(() => ReadSet(p.Value));
                if (set != null) sets[p.Name] = set;
            }

            var weapons = new Dictionary<string, GameWeapon>(StringComparer.Ordinal);
            foreach (var p in Members(root, "weapons"))
            {
                var w = Safe(() => ReadWeapon(p.Value));
                if (w != null) weapons[p.Name] = w;
            }

            var characters = new List<CharacterRecord>();
            int skipped = 0;
            foreach (var e in Items(root, "characters"))
            {
                var c = Safe(() => ReadCharacter(e));
                if (c != null) characters.Add(c);
                else skipped++;
            }
            if (skipped > 0) Logger.Log.Warn($"Character data: skipped {skipped} malformed record(s).");

            return new CharacterCatalog(game, Str(root, "game_version"), characters, sets, weapons, stats)
            {
                _icons = ReadIconSources(root),
                StatsShort = ReadStatsShort(root, stats),
            };
        }

        // The site's rule for a short name: a short English word or two ending in "%"; anything else is ignored.
        private static readonly System.Text.RegularExpressions.Regex ShortStat =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z][A-Za-z .-]{0,23}%\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static Dictionary<string, string> ReadStatsShort(JsonElement root, Dictionary<string, string> stats)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Members(root, "stats_short"))
                if (stats.ContainsKey(p.Name) && p.Value.ValueKind == JsonValueKind.String && ShortStat.IsMatch(p.Value.GetString() ?? ""))
                    map[p.Name] = p.Value.GetString();
            return map;
        }

        // icon_base / icon_ext per kind; only absolute https bases are accepted.
        private static Dictionary<IconKind, (string, string)> ReadIconSources(JsonElement root)
        {
            var map = new Dictionary<IconKind, (string, string)>();
            if (!root.TryGetProperty("icon_base", out var bases) || bases.ValueKind != JsonValueKind.Object) return map;
            root.TryGetProperty("icon_ext", out var exts);
            foreach (var (kind, name) in new[]
                     {
                         (IconKind.Character, "character"), (IconKind.Weapon, "weapon"), (IconKind.Set, "set"),
                         (IconKind.Material, "material"), (IconKind.Splash, "splash"), (IconKind.Skill, "skill"),
                     })
            {
                string b = Str(bases, name);
                if (!Uri.TryCreate(b, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                string ext = exts.ValueKind == JsonValueKind.Object ? Str(exts, name) : "";
                if (ext.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\.[A-Za-z0-9]{2,5}$")) continue;
                map[kind] = (b, ext);
            }
            return map;
        }

        private static CharacterRecord ReadCharacter(JsonElement e)
        {
            string id = Str(e, "id");
            if (id.Length == 0 && e.TryGetProperty("id", out var rawId) && rawId.ValueKind == JsonValueKind.Number)
                id = rawId.GetRawText(); // contract says string; tolerate a numeric id
            string name = Str(e, "name");
            if (id.Length == 0 || name.Length == 0) return null;

            var skills = new List<CharacterSkill>();
            foreach (var s in Items(e, "skills"))
            {
                string skillName = Str(s, "name");
                if (skillName.Length == 0) continue;
                skills.Add(new CharacterSkill
                {
                    Kind = Str(s, "kind"),
                    Name = skillName,
                    Icon = Str(s, "icon"),
                    Variant = Str(s, "variant"),
                    Short = Text(s, "short"),
                    Description = Text(s, "desc"),
                });
            }

            return new CharacterRecord
            {
                Id = id,
                Name = name,
                Icon = Str(e, "icon"),
                Splash = Str(e, "splash"),
                Rarity = Int(e, "rarity"),
                Element = Str(e, "element"),
                WeaponType = Str(e, "weapon_type"),
                Role = Text(e, "role"),
                Skills = skills,
                Build = ReadBuild(e),
                Ai = Safe(() => ReadAi(e)),
                DefaultVariant = Str(e, "default_variant"),
                Materials = Safe(() => ReadMaterials(e)),
                Leveling = Ints(e, "leveling"),
                Links = ReadLinks(e),
                // Optional and tolerant: kept only when the site itself would accept them.
                Slug = CharacterPageLink.IsValidSlug(Str(e, "slug")) ? Str(e, "slug") : "",
                PageUrl = new[] { Str(e, "page_url"), Str(e, "pageUrl") }.FirstOrDefault(CharacterPageLink.IsSitePage) ?? "",
                ReleaseDate = ReleaseDay(Str(e, "release_date")),
                ReleaseVersion = System.Text.RegularExpressions.Regex.IsMatch(Str(e, "release_version"), @"^\d{1,2}(\.\d{1,2}){0,2}$")
                    ? Str(e, "release_version") : "",
            };
        }

        private static CharacterAi ReadAi(JsonElement e)
        {
            if (!e.TryGetProperty("ai", out var a) || a.ValueKind != JsonValueKind.Object) return null;
            var ai = new CharacterAi
            {
                Model = Str(a, "model"),
                GeneratedAt = Str(a, "generated_at"),
                En = ReadAiText(a, "en"),
                Pl = ReadAiText(a, "pl"),
                Build = ReadBuild(a),
                Priority = ReadPriority(a),
                Image = Safe(() => ReadImage(a)),
                Teams = ReadTeams(a),
                StatGoals = ReadStatGoals(a),
            };
            return ai.En == null && ai.Pl == null && ai.Build == null && ai.Priority == null &&
                   ai.Image == null && ai.Teams.Count == 0 && ai.StatGoals.Count == 0 ? null : ai;
        }

        // A bad entry is skipped, never the list; the first goal per stat wins.
        private static IReadOnlyList<StatGoal> ReadStatGoals(JsonElement a)
        {
            var goals = new List<StatGoal>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in Items(a, "stat_goals"))
            {
                var goal = ReadStatGoal(g);
                if (goal == null || !seen.Add(goal.Stat)) continue;
                goals.Add(goal);
                if (goals.Count == MaxStatGoals) break;
            }
            return goals;
        }

        private static StatGoal ReadStatGoal(JsonElement g)
        {
            if (g.ValueKind != JsonValueKind.Object) return null;
            string stat = Str(g, "stat").Trim();
            if (stat.Length == 0 && g.TryGetProperty("stat", out var rawStat) && rawStat.ValueKind == JsonValueKind.Number)
                stat = rawStat.GetRawText(); // the contract says string; tolerate a numeric key (ZZZ keys are digits)
            if (stat.Length == 0 || stat.Length > 64) return null;
            if (!Bound(g, "min", out double? min) || !Bound(g, "max", out double? max)) return null;
            if ((min == null && max == null) || min > max) return null;
            string unit = "";
            if (g.TryGetProperty("unit", out var u) && u.ValueKind != JsonValueKind.Null)
                unit = u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "").Trim() : null;
            if (unit != "%" && unit != "") return null;
            return new StatGoal { Stat = stat, Min = min, Max = max, Unit = unit, Open = Bool(g, "open"), Note = Localized(g, "note") };
        }

        // Absent or null is no bound; a finite number of 0 or more is one; anything else spoils the entry.
        private static bool Bound(JsonElement g, string name, out double? value)
        {
            value = null;
            if (!g.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || double.IsNaN(d) || double.IsInfinity(d) || d < 0)
                return false;
            value = d == 0 ? 0 : d; // JSON -0 would print as "-0"
            return true;
        }

        // Members only, whole numbers 0..6 only; anything else drops that one pair.
        private static IReadOnlyDictionary<string, int> ReadRanks(JsonElement t, IReadOnlyCollection<string> members)
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in Members(t, "ranks"))
                if (members.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.Number &&
                    p.Value.TryGetInt32(out int n) && n >= 0 && n <= MaxRank)
                    ranks[p.Name] = n;
            return ranks;
        }

        // The first MaxRotations good rotations; any defect skips that rotation whole: shown as the page wrote it, or not at all.
        private static IReadOnlyList<TeamRotation> ReadRotations(JsonElement t, IReadOnlyCollection<string> members)
        {
            var rotations = new List<TeamRotation>();
            foreach (var r in Items(t, "rotations"))
            {
                var rotation = ReadRotation(r, members);
                if (rotation == null) continue;
                rotations.Add(rotation);
                if (rotations.Count == MaxRotations) break;
            }
            return rotations;
        }

        private static TeamRotation ReadRotation(JsonElement r, IReadOnlyCollection<string> members)
        {
            if (r.ValueKind != JsonValueKind.Object) return null;
            string variant = "";
            if (r.TryGetProperty("variant", out var v) && v.ValueKind != JsonValueKind.Null)
            {
                if (v.ValueKind != JsonValueKind.String) return null;
                variant = Squash(v.GetString());
                if (variant.Length > MaxVariantLength) return null;
            }
            if (!Flag(r, "covers", out bool? covers)) return null;
            if (!r.TryGetProperty("steps", out var s) || s.ValueKind != JsonValueKind.Array) return null;
            int count = s.GetArrayLength();
            if (count == 0 || count > MaxRotationSteps) return null;
            var steps = new List<RotationStep>(count);
            foreach (var e in s.EnumerateArray())
            {
                var step = ReadRotationStep(e, members);
                if (step == null) return null;
                steps.Add(step);
            }
            // A combo for no member, or without text, drops alone: its chip still shows, without the footnote.
            var combo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Members(r, "combo"))
            {
                string text = p.Value.ValueKind == JsonValueKind.String ? Squash(p.Value.GetString()) : "";
                if (members.Contains(p.Name) && text.Length > 0 && text.Length <= MaxComboLength) combo[p.Name] = text;
            }
            return new TeamRotation { Variant = variant, Steps = steps, Combo = combo, Covers = covers };
        }

        private static RotationStep ReadRotationStep(JsonElement e, IReadOnlyCollection<string> members)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("member", out var m)) return null;
            string member = m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "").Trim()
                : m.ValueKind == JsonValueKind.Number ? m.GetRawText() : ""; // ids are strings; a number is tolerated, as in members
            if (member.Length == 0 || !members.Contains(member)) return null;
            if (!e.TryGetProperty("actions", out var a) || a.ValueKind != JsonValueKind.Array) return null;
            int count = a.GetArrayLength();
            if (count == 0 || count > MaxStepActions) return null;
            var actions = new List<RotationAction>(count);
            foreach (var x in a.EnumerateArray())
            {
                var action = ReadRotationAction(x);
                if (action == null) return null;
                actions.Add(action);
            }
            if (!ReadNote(e, out string note)) return null;
            return new RotationStep { Member = member, Actions = actions, Note = note };
        }

        // A token of the grammar exactly as written; n absent or null is 1, else a whole number 1..MaxActionCount. hold and opt
        // are true, false or null; alt is another token. Any other value skips the rotation: a hold shown as a tap would mislead.
        private static RotationAction ReadRotationAction(JsonElement x)
        {
            if (x.ValueKind != JsonValueKind.Object) return null;
            string token = Str(x, "a").Trim();
            if (!RotationNotation.IsAction(token)) return null;
            int count = 1;
            if (x.TryGetProperty("n", out var n) && n.ValueKind != JsonValueKind.Null &&
                (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out count) || count < 1 || count > MaxActionCount))
                return null;
            if (!Flag(x, "hold", out bool? hold) || !Flag(x, "opt", out bool? optional)) return null;
            string alt = "";
            if (x.TryGetProperty("alt", out var a) && a.ValueKind != JsonValueKind.Null)
            {
                alt = a.ValueKind == JsonValueKind.String ? (a.GetString() ?? "").Trim() : "";
                if (!RotationNotation.IsAction(alt) || alt == token) return null;
            }
            return new RotationAction { Action = token, Count = count, Hold = hold == true, Optional = optional == true, Alt = alt };
        }

        // true, false, null or absent (null: not set); any other value spoils the rotation.
        private static bool Flag(JsonElement e, string name, out bool? value)
        {
            value = null;
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return true;
            if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False) return false;
            value = v.ValueKind == JsonValueKind.True;
            return true;
        }

        // One line of English, blank being none. Not a string, or past MaxStepNoteLength, spoils the rotation: dropping a
        // condition alone would misstate the turn.
        private static bool ReadNote(JsonElement e, out string note)
        {
            note = "";
            if (!e.TryGetProperty("note", out var v) || v.ValueKind == JsonValueKind.Null) return true;
            if (v.ValueKind != JsonValueKind.String) return false;
            note = Squash(v.GetString());
            return note.Length <= MaxStepNoteLength;
        }

        private static string Squash(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();

        private static AiImage ReadImage(JsonElement a)
        {
            if (!a.TryGetProperty("image", out var i) || i.ValueKind != JsonValueKind.Object) return null;
            string url = Str(i, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            var image = new AiImage { Url = uri.AbsoluteUri, Width = Math.Max(0, Int(i, "w")), Height = Math.Max(0, Int(i, "h")), Alt = Localized(i, "alt") };
            // The focal point counts only whole and in range; anything else leaves the crop centred.
            if (i.TryGetProperty("focal", out var f) && f.ValueKind == JsonValueKind.Object &&
                Unit(f, "x") is double x && Unit(f, "y") is double y)
            {
                image.FocalX = x;
                image.FocalY = y;
            }
            return image;
        }

        private static double? Unit(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) &&
            d >= 0 && d <= 1 ? d : null;

        // yyyy-MM-dd only, so release days compare as text.
        private static string ReleaseDay(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : "";

        // A team needs members; everything else is optional. Unknown sources are kept as labels.
        private static IReadOnlyList<AiTeam> ReadTeams(JsonElement a)
        {
            var teams = new List<AiTeam>();
            foreach (var t in Items(a, "teams"))
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var members = Strings(t, "members").Distinct(StringComparer.Ordinal).ToList();
                if (members.Count == 0) continue;
                // Roles come as a map ({"<id>": role}) or, in the teams v2 data, as a list ([{id, role}]).
                var roles = new Dictionary<string, AiLocalized>(StringComparer.Ordinal);
                foreach (var p in Members(t, "roles"))
                {
                    var role = LocalizedValue(p.Value);
                    if (!role.IsEmpty) roles[p.Name] = role;
                }
                foreach (var r in Items(t, "roles"))
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    string who = Str(r, "id");
                    var role = Localized(r, "role");
                    if (who.Length > 0 && !role.IsEmpty) roles[who] = role;
                }
                teams.Add(new AiTeam
                {
                    Name = Localized(t, "name"),
                    Source = Str(t, "source").Trim().ToLowerInvariant(),
                    Members = members,
                    Roles = roles,
                    Why = Localized(t, "why"),
                    Ranks = ReadRanks(t, members),
                    Rotations = ReadRotations(t, members),
                });
            }
            return teams;
        }

        private static AiLocalized Localized(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? LocalizedValue(v) : AiLocalized.Empty;

        private static AiLocalized LocalizedValue(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.String)
                return string.IsNullOrWhiteSpace(v.GetString()) ? AiLocalized.Empty : new AiLocalized { En = v.GetString().Trim() };
            if (v.ValueKind != JsonValueKind.Object) return AiLocalized.Empty;
            var text = new AiLocalized { En = Str(v, "en").Trim(), Pl = Str(v, "pl").Trim() };
            return text.IsEmpty ? AiLocalized.Empty : text;
        }

        private static AiPriority ReadPriority(JsonElement a)
        {
            if (!a.TryGetProperty("priority", out var p) || p.ValueKind != JsonValueKind.Object) return null;
            var pr = new AiPriority
            {
                Sets = Strings(p, "sets"),
                Weapons = Strings(p, "weapons"),
                Skills = Ints(p, "skills"),
                Progression = Ints(p, "progression"),
            };
            return pr.IsEmpty ? null : pr;
        }

        private static CharacterMaterials ReadMaterials(JsonElement e)
        {
            if (!e.TryGetProperty("materials", out var m) || m.ValueKind != JsonValueKind.Object) return null;
            IReadOnlyList<MaterialCost> Costs(string name) => Items(m, name)
                .Where(x => x.ValueKind == JsonValueKind.Object && Str(x, "name").Length > 0)
                .Select(x => new MaterialCost { Name = Str(x, "name"), Count = Int(x, "count"), Icon = Str(x, "icon") })
                .ToList();
            var mats = new CharacterMaterials { Ascension = Costs("ascension"), Skills = Costs("skills") };
            return mats.Ascension.Count == 0 && mats.Skills.Count == 0 ? null : mats;
        }

        // Only absolute https links reach the UI; anything else in the data is dropped here.
        private static IReadOnlyList<ExternalLink> ReadLinks(JsonElement e) => Items(e, "links")
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new ExternalLink { Site = Str(x, "site"), Label = Str(x, "label"), Url = Str(x, "url") })
            .Where(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            .Select(l => { if (l.Label.Length == 0) l.Label = l.Site; return l; })
            .Where(l => l.Label.Length > 0)
            .ToList();

        private static IReadOnlyList<int> Ints(JsonElement e, string name) =>
            Items(e, name).Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                .Select(x => x.GetInt32()).Where(i => i >= 0).Distinct().ToList();

        private static AiText ReadAiText(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Object) return null;
            var skills = new Dictionary<int, string>();
            foreach (var p in Members(t, "skills"))
            {
                if (p.Value.ValueKind == JsonValueKind.String &&
                    int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
                {
                    string line = p.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(line)) skills[i] = line;
                }
            }
            var guide = Items(t, "guide")
                .Where(g => g.ValueKind == JsonValueKind.Object && Str(g, "heading").Length > 0 && Str(g, "body").Length > 0)
                .Select(g => new GuideSection { Heading = Str(g, "heading"), Body = Str(g, "body") })
                .ToList();
            var text = new AiText { Skills = skills, Playstyle = Strings(t, "playstyle"), BuildNote = Str(t, "build_note"), Guide = guide };
            return text.IsEmpty ? null : text;
        }

        private static CharacterBuild ReadBuild(JsonElement e)
        {
            if (!e.TryGetProperty("build", out var b) || b.ValueKind != JsonValueKind.Object) return null;
            return new CharacterBuild
            {
                Sets = Items(b, "sets")
                    .Select(s => new BuildSetChoice { Pieces = Int(s, "pieces"), Ids = Strings(s, "ids"), Combine = Bool(s, "combine") })
                    .Where(s => s.Ids.Count > 0).ToList(),
                MainStats = Items(b, "main_stats")
                    .Select(m => new BuildMainStat { Slot = Str(m, "slot"), Stats = Strings(m, "stats") })
                    .Where(m => m.Stats.Count > 0).ToList(),
                SubStats = Strings(b, "sub_stats"),
                Weapons = Strings(b, "weapons"),
                Teammates = Strings(b, "teammates"),
            };
        }

        private static GearSet ReadSet(JsonElement e)
        {
            string name = Str(e, "name");
            if (name.Length == 0) return null;
            return new GearSet
            {
                Name = name,
                Icon = Str(e, "icon"),
                Kind = Str(e, "kind"),
                Bonuses = Items(e, "bonuses")
                    .Select(b => new GearSetBonus { Pieces = Int(b, "pieces"), Text = Text(b, "text") })
                    .Where(b => b.Text != null).ToList(),
            };
        }

        private static GameWeapon ReadWeapon(JsonElement e)
        {
            string name = Str(e, "name");
            if (name.Length == 0) return null;
            return new GameWeapon
            {
                Name = name,
                Icon = Str(e, "icon"),
                Rarity = Int(e, "rarity"),
                Type = Str(e, "type"),
                PassiveName = Str(e, "passive_name"),
                Passive = Text(e, "passive"),
            };
        }

        // ── tolerant readers: a wrong type yields a default, never an exception ──

        private static T Safe<T>(Func<T> read) where T : class
        {
            try { return read(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException || ex is KeyNotFoundException)
            {
                return null;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int Int(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            if (v.ValueKind == JsonValueKind.String &&
                int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
            return 0;
        }

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static GameText Text(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
                return null;
            string t = Str(v, "t");
            if (t.Length == 0) return null;
            var p = new List<double>();
            foreach (var n in Items(v, "p"))
                p.Add(n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out double d) ? d : double.NaN);
            return new GameText(t, p);
        }

        private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
            Items(e, name).Where(x => x.ValueKind == JsonValueKind.String || x.ValueKind == JsonValueKind.Number)
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
                .Where(s => !string.IsNullOrEmpty(s)).ToList();

        private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        private static IEnumerable<JsonProperty> Members(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject() : Enumerable.Empty<JsonProperty>();
    }
}
