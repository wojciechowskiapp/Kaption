using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Text;

namespace GI_Subtitles.Services.Characters
{
    public sealed class OverviewSkill
    {
        public string KindKey { get; set; } = "";
        public string Kind { get; set; } = "";
        /// <summary>Translated name when the pack has one, else English.</summary>
        public string Name { get; set; } = "";
        /// <summary>The name the game shows (English): the reference players look for.</summary>
        public string EnglishName { get; set; } = "";
        /// <summary>The game's shorthand written after the name outside its card: "(E)", "(Q)", "(C3)"; "" for none.</summary>
        public string Marker { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
        public string Short { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>One AI-written line, empty when there is none.</summary>
        public string AiSummary { get; set; } = "";
        /// <summary>A pack is loaded but has no translation of the description: it shows in English.</summary>
        public bool DescriptionInEnglish { get; set; }
    }

    public sealed class OverviewSetBonus
    {
        public int Pieces { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class OverviewSet
    {
        /// <summary>Translated name when the pack has one, else English.</summary>
        public string Name { get; set; } = "";
        /// <summary>The name the game shows; "" in hand-built models, where <see cref="Name"/> stands in.</summary>
        public string EnglishName { get; set; } = "";
        public string IconUrl { get; set; }
        /// <summary>Local file, set from the image cache; null until downloaded.</summary>
        public string IconPath { get; set; }
        /// <summary>"N: text" lines for plain text; <see cref="BonusItems"/> is the structured form.</summary>
        public IReadOnlyList<string> Bonuses { get; set; } = Array.Empty<string>();
        public IReadOnlyList<OverviewSetBonus> BonusItems { get; set; } = Array.Empty<OverviewSetBonus>();
    }

    /// <summary>One line of the Tips checklist: what to pick first, and where it came from.</summary>
    public sealed class OverviewHighlight
    {
        public string LabelKey { get; set; } = "";
        /// <summary>Game terms as the game shows them (English), skill names with their type marker.</summary>
        public string Text { get; set; } = "";
        /// <summary>The same terms in the pack's language, shown smaller; "" when they read the same.</summary>
        public string Gloss { get; set; } = "";
        public bool IsAi { get; set; }
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    public sealed class OverviewMaterial
    {
        /// <summary>Translated name when the pack has one, else English.</summary>
        public string Name { get; set; } = "";
        /// <summary>The name the game shows; "" in hand-built models, where <see cref="Name"/> stands in.</summary>
        public string EnglishName { get; set; } = "";
        public int Count { get; set; }
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    public sealed class OverviewLink
    {
        public string Site { get; set; } = "";
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>The site's mark, drawn by the app (<see cref="LinkHosts"/>); nothing is fetched from the site.</summary>
        public string IconKey { get; set; } = LinkHosts.Generic;
        public string Monogram { get; set; } = "";
    }

    public sealed class OverviewGuideSection
    {
        public string Heading { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public sealed class OverviewTeammate
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    public sealed class OverviewTeamMember
    {
        public string Id { get; set; } = "";
        /// <summary>English, like every character name.</summary>
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    /// <summary>A team comp; <see cref="Source"/> is "official", "ai" or another provider's label.</summary>
    public sealed class OverviewTeam
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public string Why { get; set; } = "";
        public IReadOnlyList<OverviewTeamMember> Members { get; set; } = Array.Empty<OverviewTeamMember>();
    }

    /// <summary>Another character's team that includes this one: the host, the team's name and this character's role.</summary>
    public sealed class OverviewTeamHost
    {
        public string HostId { get; set; } = "";
        /// <summary>English, like every character name.</summary>
        public string HostName { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
        public string TeamName { get; set; } = "";
        /// <summary>This character's role in the host's team, "" when the data names none.</summary>
        public string Role { get; set; } = "";
    }

    /// <summary>One recommended slot of N pieces with its alternatives, e.g. 4 pieces: A or B.</summary>
    public sealed class OverviewSetGroup
    {
        public int Pieces { get; set; }
        /// <summary>True: all options are worn together (2+2); false: pick one.</summary>
        public bool Combine { get; set; }
        public IReadOnlyList<OverviewSet> Options { get; set; } = Array.Empty<OverviewSet>();
        public string PiecesText => Combine ? string.Join(" + ", Options.Select(_ => Pieces)) : Pieces.ToString();
    }

    public sealed class OverviewMainStat
    {
        public string SlotKey { get; set; } = "";
        public string Slot { get; set; } = "";
        /// <summary>Translated stat names; <see cref="EnglishStats"/> holds what the game shows.</summary>
        public IReadOnlyList<string> Stats { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> EnglishStats { get; set; } = Array.Empty<string>();
    }

    public sealed class OverviewWeapon
    {
        /// <summary>Translated name when the pack has one, else English.</summary>
        public string Name { get; set; } = "";
        /// <summary>The name the game shows; "" in hand-built models, where <see cref="Name"/> stands in.</summary>
        public string EnglishName { get; set; } = "";
        public string EnglishPassiveName { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
        public int Rarity { get; set; }
        public string RarityText { get; set; } = "";
        public string PassiveName { get; set; } = "";
        public string Passive { get; set; } = "";
    }

    /// <summary>Everything the overview view shows, already translated. No WPF types.</summary>
    public sealed class CharacterOverviewModel
    {
        public string CharacterId { get; set; } = "";
        /// <summary>Wire id of the game the catalog came from ("genshin", "starrail", "zzz").</summary>
        public string Game { get; set; } = "";
        /// <summary>The kit shown ("enhanced", "potential", "original"), or "" when the record has one kit.</summary>
        public string Variant { get; set; } = "";
        /// <summary>Kits the player can switch between; empty for a plain record.</summary>
        public IReadOnlyList<string> Variants { get; set; } = Array.Empty<string>();
        /// <summary>English proper name; packs carry machine translations of names, never shown.</summary>
        public string Name { get; set; } = "";
        public string EnglishName { get; set; } = "";
        /// <summary>The subtitles' Polish word for the protagonist title ("Podróżnik"), else "".</summary>
        public string NameGloss { get; set; } = "";
        /// <summary>English element label from the data ("Hydro", "Quantum"), for colours and filters.</summary>
        public string ElementKey { get; set; } = "";
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
        /// <summary>Full-body art; null when the game has no verified source.</summary>
        public string SplashUrl { get; set; }
        public string SplashPath { get; set; }
        /// <summary>The updater's ambience image (AI, no likeness); null when there is none.</summary>
        public string AmbienceUrl { get; set; }
        public string AmbiencePath { get; set; }
        public string AmbienceAlt { get; set; } = "";
        /// <summary>Width / height of the ambience image, 0 when unknown.</summary>
        public double AmbienceAspect { get; set; }
        /// <summary>Where the header's crop of the ambience image is centred (0 to 1 each way); the data's focal point
        /// when it has one, else a little above the middle, where these scenes keep their subject.</summary>
        public double AmbienceFocalX { get; set; } = 0.5;
        public double AmbienceFocalY { get; set; } = DefaultFocalY;
        public const double DefaultFocalY = 0.42;
        public int Rarity { get; set; }
        public string RarityText { get; set; } = "";
        public string Element { get; set; } = "";
        public string WeaponType { get; set; } = "";
        public string Role { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public IReadOnlyList<OverviewSkill> Skills { get; set; } = Array.Empty<OverviewSkill>();
        public IReadOnlyList<OverviewSetGroup> SetGroups { get; set; } = Array.Empty<OverviewSetGroup>();
        public IReadOnlyList<OverviewMainStat> MainStats { get; set; } = Array.Empty<OverviewMainStat>();
        public IReadOnlyList<string> SubStats { get; set; } = Array.Empty<string>();
        /// <summary><see cref="SubStats"/> as the game names them.</summary>
        public IReadOnlyList<string> SubStatsEnglish { get; set; } = Array.Empty<string>();
        public IReadOnlyList<OverviewWeapon> Weapons { get; set; } = Array.Empty<OverviewWeapon>();
        public IReadOnlyList<string> Teammates { get; set; } = Array.Empty<string>();
        public IReadOnlyList<OverviewTeammate> TeammateCards { get; set; } = Array.Empty<OverviewTeammate>();
        /// <summary>Team comps, official first, then AI, then other sources.</summary>
        public IReadOnlyList<OverviewTeam> Teams { get; set; } = Array.Empty<OverviewTeam>();
        /// <summary>Other characters' teams with a place for this one ("Gra też w drużynach"), the newest host first.</summary>
        public IReadOnlyList<OverviewTeamHost> TeamHosts { get; set; } = Array.Empty<OverviewTeamHost>();
        /// <summary>Skills to level first, English names in order; the game's order, else the AI's.</summary>
        public IReadOnlyList<string> Leveling { get; set; } = Array.Empty<string>();
        public bool LevelingIsAi { get; set; }

        // Tips tab (Phase C3)
        public IReadOnlyList<OverviewHighlight> Highlights { get; set; } = Array.Empty<OverviewHighlight>();
        public IReadOnlyList<OverviewMaterial> MaterialsAscension { get; set; } = Array.Empty<OverviewMaterial>();
        public IReadOnlyList<OverviewMaterial> MaterialsSkills { get; set; } = Array.Empty<OverviewMaterial>();
        public IReadOnlyList<OverviewLink> Links { get; set; } = Array.Empty<OverviewLink>();
        public IReadOnlyList<OverviewGuideSection> Guide { get; set; } = Array.Empty<OverviewGuideSection>();
        /// <summary>The character's own page on kaption.one for a Polish interface (<see cref="CharacterPageLink"/>).</summary>
        public string PageUrlPl { get; set; } = "";
        /// <summary>The same page for any other interface language (the site's English page).</summary>
        public string PageUrlEn { get; set; } = "";

        /// <summary>The page for <paramref name="uiLanguage"/>, else the other language's, else "".</summary>
        public string PageUrlFor(string uiLanguage)
        {
            string preferred = CharacterPageLink.IsPolish(uiLanguage) ? PageUrlPl : PageUrlEn;
            return !string.IsNullOrEmpty(preferred) ? preferred : (PageUrlPl.Length > 0 ? PageUrlPl : PageUrlEn) ?? "";
        }
        public bool HasTips => Highlights.Count > 0 || Links.Count > 0 || Guide.Count > 0 ||
                               MaterialsAscension.Count > 0 || MaterialsSkills.Count > 0;
        public bool HasOfficialBuild { get; set; }
        /// <summary>A build is shown: the official one, or else an AI suggestion.</summary>
        public bool HasBuild { get; set; }
        public bool BuildIsAi { get; set; }
        public string BuildNote { get; set; } = "";
        public IReadOnlyList<string> Playstyle { get; set; } = Array.Empty<string>();
        public bool HasAi => Playstyle.Count > 0 || BuildIsAi || Skills.Any(s => s.AiSummary.Length > 0);

        public string Tagline =>
            string.Join(" · ", new[] { Element, WeaponType }.Where(s => !string.IsNullOrEmpty(s)));

        /// <summary>For the copy button. Section labels come from the caller (UI language).</summary>
        public string ToPlainText(Func<string, string> label = null)
        {
            string L(string key, string fallback) => label?.Invoke(key) ?? fallback;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" · ", new[] { Name, Element, WeaponType }.Where(s => !string.IsNullOrEmpty(s))));
            if (!string.IsNullOrEmpty(Role)) sb.AppendLine(Role);
            foreach (var s in Skills)
            {
                sb.AppendLine().AppendLine(Both(s.EnglishName, s.Name));
                sb.AppendLine(string.IsNullOrEmpty(s.Short) ? s.Description : s.Short);
            }
            if (HasOfficialBuild)
            {
                sb.AppendLine().AppendLine(L("Char_Section_Build", "Recommended build"));
                foreach (var g in SetGroups)
                    sb.AppendLine($"{g.PiecesText}: {string.Join(g.Combine ? " + " : " / ", g.Options.Select(o => Both(o.EnglishName, o.Name)))}");
                foreach (var m in MainStats)
                    sb.AppendLine($"{m.Slot}: {string.Join(" / ", m.Stats.Select((s, i) => Both(i < m.EnglishStats.Count ? m.EnglishStats[i] : "", s)))}");
                if (SubStats.Count > 0)
                    sb.AppendLine(string.Join(", ", SubStats.Select((s, i) => Both(i < SubStatsEnglish.Count ? SubStatsEnglish[i] : "", s))));
                if (Weapons.Count > 0) sb.AppendLine(string.Join(" / ", Weapons.Select(w => Both(w.EnglishName, w.Name))));
            }
            return sb.ToString().TrimEnd();
        }

        // "Golden Troupe (Złota Trupa)": the game's name first, the translation when it differs.
        private static string Both(string english, string translated)
        {
            if (string.IsNullOrWhiteSpace(english)) return translated ?? "";
            return string.IsNullOrWhiteSpace(translated) || string.Equals(english, translated, StringComparison.OrdinalIgnoreCase)
                ? english : $"{english} ({translated})";
        }
    }

    /// <summary>Builds the translated overview from catalog data. Pure; safe on any thread.</summary>
    public static class CharacterOverviewBuilder
    {
        private static readonly System.Text.RegularExpressions.Regex CompositeName =
            new System.Text.RegularExpressions.Regex(@"^(.+?) \(([^()]+)\)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Stars, except ZZZ, whose data stores ranks B/A/S as 2/3/4.</summary>
        public static string RarityText(string game, int rarity)
        {
            if (rarity <= 0) return "";
            if (string.Equals(game, "zzz", StringComparison.OrdinalIgnoreCase))
                return rarity >= 4 ? "S" : rarity == 3 ? "A" : "B";
            return new string('★', Math.Min(rarity, 6));
        }

        /// <param name="variant">Kit to show; null = the game's default for that record.</param>
        public static CharacterOverviewModel Build(CharacterRecord record, CharacterCatalog catalog, ITranslationLookup lookup,
                                                   string variant = null)
        {
            if (record == null) return null;
            catalog = catalog ?? CharacterCatalog.Empty;
            var variants = record.Variants;
            string chosen = variants.Count == 0 ? ""
                : variants.Contains(variant ?? "") ? variant
                : variants.Contains(record.DefaultVariant) ? record.DefaultVariant
                : variants[0];

            string Exact(string english) =>
                lookup != null && lookup.TryTranslateExact(english, out string t) && !string.IsNullOrEmpty(t) ? t : null;

            string T(string english)
            {
                if (string.IsNullOrEmpty(english)) return "";
                string whole = Exact(english);
                if (whole != null) return whole;
                // Composite names ("Traveler (Hydro)") are not pack keys, but both parts are.
                var m = CompositeName.Match(english);
                return m.Success
                    ? $"{Exact(m.Groups[1].Value) ?? m.Groups[1].Value} ({Exact(m.Groups[2].Value) ?? m.Groups[2].Value})"
                    : english;
            }

            string F(GameText g)
            {
                if (g == null || string.IsNullOrEmpty(g.Template)) return "";
                string translated = T(g.Template).Replace("\\n", "\n");
                return GameTextFormatter.StripMarkup(GameTextFormatter.FillFromParams(g.Template, translated, g.Params));
            }

            // The pack had nothing for this text (the English stays on screen).
            bool Untranslated(GameText g) =>
                g != null && !string.IsNullOrWhiteSpace(g.Template) && Exact(g.Template) == null;

            string Stat(string key) =>
                catalog.Stats.TryGetValue(key ?? "", out string label) && !string.IsNullOrEmpty(label) ? T(label) : key ?? "";
            string StatEnglish(string key) =>
                catalog.Stats.TryGetValue(key ?? "", out string label) && !string.IsNullOrEmpty(label) ? label : key ?? "";

            // Skill names in any text get the game's type marker after them: "Fluttering Hasode (E)".
            string[] markers = SkillMarkers.ForRecord(catalog.Game, record.Skills, chosen);
            var references = new List<SkillReference>();
            for (int i = 0; i < markers.Length; i++)
            {
                if (markers[i].Length == 0) continue;
                references.Add(new SkillReference(record.Skills[i].Name, markers[i], i));
                string translatedName = T(record.Skills[i].Name);
                if (translatedName != record.Skills[i].Name) references.Add(new SkillReference(translatedName, markers[i], i));
            }
            string A(string text, int self = -1) => SkillMarkers.Annotate(text, references, self);

            var model = new CharacterOverviewModel
            {
                CharacterId = record.Id,
                Game = catalog.Game ?? "",
                Name = record.Name ?? "",
                EnglishName = record.Name ?? "",
                NameGloss = CharacterNames.Gloss(record.Name, Exact),
                ElementKey = record.Element,
                IconUrl = catalog.IconUrl(IconKind.Character, record.Icon),
                SplashUrl = catalog.IconUrl(IconKind.Splash, record.Splash),
                Rarity = record.Rarity,
                RarityText = RarityText(catalog.Game, record.Rarity),
                Element = T(record.Element),
                WeaponType = T(record.WeaponType),
                Role = F(record.Role),
                GameVersion = catalog.GameVersion,
                Variant = chosen,
                Variants = variants,
                // AI skill lines are keyed by the index into the FULL skills list, so filter after indexing.
                Skills = record.Skills.Select((s, i) => (s, i))
                    .Where(x => x.s.Variant.Length == 0 || x.s.Variant == chosen)
                    .Select(x => new OverviewSkill
                    {
                        Kind = x.s.Kind,
                        KindKey = "Char_Kind_" + x.s.Kind,
                        Name = T(x.s.Name),
                        EnglishName = x.s.Name,
                        Marker = markers[x.i],
                        IconUrl = catalog.IconUrl(IconKind.Skill, x.s.Icon),
                        Short = A(F(x.s.Short), x.i),
                        Description = A(F(x.s.Description), x.i),
                        DescriptionInEnglish = lookup != null && Untranslated(x.s.Description),
                        AiSummary = A(AiLine(record.Ai, x.i), x.i),
                    }).ToList(),
                Playstyle = PickAiText(record.Ai, t => t.Playstyle.Count > 0)?.Playstyle
                                .Select(p => A(GameTextFormatter.StripMarkup(p))).ToList()
                            ?? (IReadOnlyList<string>)Array.Empty<string>(),
                Teams = Teams(record.Ai, catalog, A),
                TeamHosts = TeamHosts(record, catalog, A),
            };
            var image = record.Ai?.Image;
            if (image != null)
            {
                model.AmbienceUrl = image.Url;
                model.AmbienceAlt = GameTextFormatter.StripMarkup(image.Alt.Pick());
                model.AmbienceAspect = image.Width > 0 && image.Height > 0 ? (double)image.Width / image.Height : 0;
                if (image.FocalX is double fx && image.FocalY is double fy)
                {
                    model.AmbienceFocalX = fx;
                    model.AmbienceFocalY = fy;
                }
            }

            // Official data always wins; the AI build only fills a gap.
            var build = record.Build;
            if (build == null && record.Ai?.Build != null)
            {
                build = record.Ai.Build;
                model.BuildIsAi = true;
                model.BuildNote = A(GameTextFormatter.StripMarkup(
                    PickAiText(record.Ai, t => t.BuildNote.Length > 0)?.BuildNote ?? ""));
            }
            string SkillRef(int i) => i >= 0 && i < record.Skills.Count ? SkillMarkers.WithMarker(record.Skills[i].Name, markers[i]) : "";
            if (build == null)
            {
                FillTips(model, record, catalog, T, SkillRef, A); // links, materials and AI picks exist without a build
                return model;
            }

            model.SetGroups = build.Sets
                .Select(choice => new OverviewSetGroup
                {
                    Pieces = choice.Pieces,
                    Combine = choice.Combine,
                    Options = choice.Ids
                        .Where(id => catalog.Sets.ContainsKey(id))
                        .Select(id => catalog.Sets[id])
                        .Select(set => new OverviewSet
                        {
                            Name = T(set.Name),
                            EnglishName = set.Name,
                            IconUrl = catalog.IconUrl(IconKind.Set, set.Icon),
                            Bonuses = set.Bonuses.Select(b => $"{b.Pieces}: {F(b.Text)}").ToList(),
                            BonusItems = set.Bonuses.Select(b => new OverviewSetBonus { Pieces = b.Pieces, Text = F(b.Text) }).ToList(),
                        }).ToList(),
                })
                .Where(g => g.Options.Count > 0).ToList();

            model.MainStats = build.MainStats.Select(m => new OverviewMainStat
            {
                Slot = m.Slot,
                SlotKey = "Char_Slot_" + m.Slot,
                Stats = m.Stats.Select(Stat).ToList(),
                EnglishStats = m.Stats.Select(StatEnglish).ToList(),
            }).ToList();

            model.SubStats = build.SubStats.Select(Stat).ToList();
            model.SubStatsEnglish = build.SubStats.Select(StatEnglish).ToList();

            model.Weapons = build.Weapons
                .Where(id => catalog.Weapons.ContainsKey(id))
                .Select(id => catalog.Weapons[id])
                .Select(w => new OverviewWeapon
                {
                    Name = T(w.Name),
                    EnglishName = w.Name,
                    EnglishPassiveName = w.PassiveName,
                    IconUrl = catalog.IconUrl(IconKind.Weapon, w.Icon),
                    Rarity = w.Rarity,
                    RarityText = RarityText(catalog.Game, w.Rarity),
                    PassiveName = T(w.PassiveName),
                    Passive = F(w.Passive),
                }).ToList();

            var mates = build.Teammates.Select(catalog.Find).Where(c => c != null).ToList();
            model.Teammates = mates.Select(c => c.Name).ToList();
            model.TeammateCards = mates.Select(c => new OverviewTeammate
            {
                Id = c.Id,
                Name = c.Name,
                IconUrl = catalog.IconUrl(IconKind.Character, c.Icon),
            }).ToList();

            model.HasBuild = model.SetGroups.Count > 0 || model.MainStats.Count > 0 ||
                             model.SubStats.Count > 0 || model.Weapons.Count > 0;
            model.HasOfficialBuild = model.HasBuild && !model.BuildIsAi;
            if (!model.HasBuild) model.BuildIsAi = false;
            FillTips(model, record, catalog, T, SkillRef, A);
            return model;
        }

        // Every name a player has to find in the game's UI reads as the game shows it (English), the translation
        // below it as a gloss; skill names carry their type marker. Explanations stay translated.
        private static void FillTips(CharacterOverviewModel model, CharacterRecord record, CharacterCatalog catalog,
                                     Func<string, string> T, Func<int, string> skillRef, Func<string, int, string> annotate)
        {
            var tips = new List<OverviewHighlight>();
            var priority = record.Ai?.Priority;
            string SkillName(int i) => i >= 0 && i < record.Skills.Count ? record.Skills[i].Name : "";

            if (model.HasBuild)
            {
                var g = model.SetGroups.FirstOrDefault();
                if (g != null && g.Options.Count > 0)
                {
                    var shown = g.Combine ? g.Options : g.Options.Take(1).ToList();
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_Set",
                        Text = string.Join(" + ", shown.Select(o => EnglishOr(o.EnglishName, o.Name))),
                        Gloss = Gloss(shown.Select(o => (EnglishOr(o.EnglishName, o.Name), o.Name)), " + "),
                        IconUrl = g.Options[0].IconUrl,
                        IsAi = model.BuildIsAi,
                    });
                }
                // Percent stats read as the game's short names ("ATK%"), which need no gloss (lead's rule).
                var primaries = model.MainStats.Where(m => m.Stats.Count > 0)
                    .Select(m => (English: GI_Subtitles.Views.CharacterOverviewViewModel.ShortStatName(m.EnglishStats.Count > 0 ? m.EnglishStats[0] : m.Stats[0]),
                                  Translated: m.Stats[0]))
                    .Select(s => s.English is "ATK%" or "DEF%" or "HP%" ? (s.English, Translated: s.English) : s)
                    .Where(s => !string.IsNullOrEmpty(s.English)).ToList();
                if (primaries.Count > 0)
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_MainStats",
                        Text = string.Join(" · ", primaries.Select(s => s.English)),
                        Gloss = Gloss(primaries, " · "),
                        IsAi = model.BuildIsAi,
                    });
                if (model.Weapons.Count > 0)
                {
                    var w = model.Weapons[0];
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_Weapon",
                        Text = EnglishOr(w.EnglishName, w.Name),
                        Gloss = Gloss(new[] { (EnglishOr(w.EnglishName, w.Name), w.Name) }, ""),
                        IconUrl = w.IconUrl,
                        IsAi = model.BuildIsAi,
                    });
                }
            }
            else if (priority != null)
            {
                var set = priority.Sets.Where(catalog.Sets.ContainsKey).Select(id => catalog.Sets[id]).FirstOrDefault();
                if (set != null)
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_Set", Text = set.Name, Gloss = Gloss(new[] { (set.Name, T(set.Name)) }, ""),
                        IconUrl = catalog.IconUrl(IconKind.Set, set.Icon), IsAi = true,
                    });
                var weapon = priority.Weapons.Where(catalog.Weapons.ContainsKey).Select(id => catalog.Weapons[id]).FirstOrDefault();
                if (weapon != null)
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_Weapon", Text = weapon.Name, Gloss = Gloss(new[] { (weapon.Name, T(weapon.Name)) }, ""),
                        IconUrl = catalog.IconUrl(IconKind.Weapon, weapon.Icon), IsAi = true,
                    });
            }

            // Leveling: the game's own order wins; the AI order only fills a gap.
            var order = record.Leveling.Where(i => SkillName(i).Length > 0).ToList();
            bool levelingIsAi = false;
            if (order.Count == 0 && priority != null)
            {
                order = priority.Skills.Where(i => SkillName(i).Length > 0).ToList();
                levelingIsAi = true;
            }
            if (order.Count > 0)
                tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_Leveling", Text = string.Join(" → ", order.Select(skillRef)), IsAi = levelingIsAi });
            model.Leveling = order.Select(SkillName).ToList();
            model.LevelingIsAi = order.Count > 0 && levelingIsAi;

            var worth = priority?.Progression.Where(i => SkillName(i).Length > 0).ToList();
            if (worth != null && worth.Count > 0)
                tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_WorthIt", Text = string.Join(", ", worth.Select(skillRef)), IsAi = true });

            model.Highlights = tips;

            IReadOnlyList<OverviewMaterial> Mats(IReadOnlyList<MaterialCost> list) => (list ?? Array.Empty<MaterialCost>())
                .Select(mc => new OverviewMaterial
                {
                    Name = T(mc.Name), EnglishName = mc.Name, Count = mc.Count, IconUrl = catalog.IconUrl(IconKind.Material, mc.Icon),
                })
                .ToList();
            model.MaterialsAscension = Mats(record.Materials?.Ascension);
            model.MaterialsSkills = Mats(record.Materials?.Skills);

            model.Links = record.Links.Select(l => new OverviewLink
            {
                Site = l.Site,
                Label = l.Label,
                Url = l.Url,
                IconKey = LinkHosts.KeyFor(l.Url),
                Monogram = LinkHosts.MonogramFor(l.Url),
            }).ToList();

            var guide = PickAiText(record.Ai, t => t.Guide.Count > 0)?.Guide ?? Array.Empty<GuideSection>();
            model.Guide = guide.Select(s => new OverviewGuideSection
            {
                Heading = GameTextFormatter.StripMarkup(s.Heading),
                Body = annotate(GameTextFormatter.StripMarkup(s.Body), -1),
            }).ToList();

            model.PageUrlPl = CharacterPageLink.For(record, catalog, "pl-PL");
            model.PageUrlEn = CharacterPageLink.For(record, catalog, "en-US");
        }

        private static string EnglishOr(string english, string translated) =>
            string.IsNullOrWhiteSpace(english) ? translated ?? "" : english;

        // The translated names joined, or "" when every one reads the same as the English.
        private static string Gloss(IEnumerable<(string English, string Translated)> names, string separator)
        {
            var list = names.ToList();
            bool differs = list.Any(n => !string.IsNullOrWhiteSpace(n.Translated) &&
                                         !string.Equals(n.English, n.Translated, StringComparison.OrdinalIgnoreCase));
            return differs
                ? string.Join(separator, list.Select(n => string.IsNullOrWhiteSpace(n.Translated) ? n.English : n.Translated))
                : "";
        }

        // Members missing from the roster are dropped; a team needs two left to be worth showing. The order is the one the
        // reverse index uses, so "the host's first team" is the first card on the host's page.
        private static IReadOnlyList<OverviewTeam> Teams(CharacterAi ai, CharacterCatalog catalog, Func<string, int, string> annotate) =>
            CharacterTeamIndex.ShownTeams(ai, catalog)
                .Select(t => new OverviewTeam
                {
                    Name = GameTextFormatter.StripMarkup(t.Name.Pick()),
                    Source = t.Source,
                    Why = annotate(GameTextFormatter.StripMarkup(t.Why.Pick()), -1),
                    Members = t.Members.Select(catalog.Find).Where(c => c != null).Select(c => new OverviewTeamMember
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Role = t.Roles.TryGetValue(c.Id, out var role) ? annotate(GameTextFormatter.StripMarkup(role.Pick()), -1) : "",
                        IconUrl = catalog.IconUrl(IconKind.Character, c.Icon),
                    }).ToList(),
                })
                .ToList();

        // The reverse index, one row per host: who, which team, and this character's role in it.
        private static IReadOnlyList<OverviewTeamHost> TeamHosts(CharacterRecord record, CharacterCatalog catalog, Func<string, int, string> annotate) =>
            CharacterTeamIndex.HostsOf(catalog, record.Id)
                .Select(h => new OverviewTeamHost
                {
                    HostId = h.Host.Id,
                    HostName = h.Host.Name,
                    IconUrl = catalog.IconUrl(IconKind.Character, h.Host.Icon),
                    TeamName = GameTextFormatter.StripMarkup(h.Team.Name.Pick()),
                    Role = h.Team.Roles.TryGetValue(record.Id, out var role) ? annotate(GameTextFormatter.StripMarkup(role.Pick()), -1) : "",
                })
                .ToList();

        // Polish first (the only target language today), English as the fallback.
        private static AiText PickAiText(CharacterAi ai, Func<AiText, bool> has) =>
            ai == null ? null : ai.Pl != null && has(ai.Pl) ? ai.Pl : ai.En != null && has(ai.En) ? ai.En : null;

        private static string AiLine(CharacterAi ai, int skillIndex)
        {
            var text = PickAiText(ai, t => t.Skills.ContainsKey(skillIndex));
            return text == null ? "" : GameTextFormatter.StripMarkup(text.Skills[skillIndex]);
        }
    }
}
