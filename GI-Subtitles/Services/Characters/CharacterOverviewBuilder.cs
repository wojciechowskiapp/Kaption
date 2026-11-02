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
        public string Name { get; set; } = "";
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
        public string Text { get; set; } = "";
        public bool IsAi { get; set; }
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    public sealed class OverviewMaterial
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
    }

    public sealed class OverviewLink
    {
        public string Site { get; set; } = "";
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>The site's /favicon.ico; null for a URL without a host.</summary>
        public string IconUrl { get; set; }
        public string IconPath { get; set; }
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
        public IReadOnlyList<string> Stats { get; set; } = Array.Empty<string>();
    }

    public sealed class OverviewWeapon
    {
        public string Name { get; set; } = "";
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
        public IReadOnlyList<OverviewWeapon> Weapons { get; set; } = Array.Empty<OverviewWeapon>();
        public IReadOnlyList<string> Teammates { get; set; } = Array.Empty<string>();
        public IReadOnlyList<OverviewTeammate> TeammateCards { get; set; } = Array.Empty<OverviewTeammate>();
        /// <summary>Team comps, official first, then AI, then other sources.</summary>
        public IReadOnlyList<OverviewTeam> Teams { get; set; } = Array.Empty<OverviewTeam>();
        /// <summary>Skills to level first, English names in order; the game's order, else the AI's.</summary>
        public IReadOnlyList<string> Leveling { get; set; } = Array.Empty<string>();
        public bool LevelingIsAi { get; set; }

        // Tips tab (Phase C3)
        public IReadOnlyList<OverviewHighlight> Highlights { get; set; } = Array.Empty<OverviewHighlight>();
        public IReadOnlyList<OverviewMaterial> MaterialsAscension { get; set; } = Array.Empty<OverviewMaterial>();
        public IReadOnlyList<OverviewMaterial> MaterialsSkills { get; set; } = Array.Empty<OverviewMaterial>();
        public IReadOnlyList<OverviewLink> Links { get; set; } = Array.Empty<OverviewLink>();
        public IReadOnlyList<OverviewGuideSection> Guide { get; set; } = Array.Empty<OverviewGuideSection>();
        /// <summary>The character's page on kaption.one (the index with the name prefilled).</summary>
        public string PageUrl { get; set; } = "";
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
                sb.AppendLine().AppendLine(s.Name);
                sb.AppendLine(string.IsNullOrEmpty(s.Short) ? s.Description : s.Short);
            }
            if (HasOfficialBuild)
            {
                sb.AppendLine().AppendLine(L("Char_Section_Build", "Recommended build"));
                foreach (var g in SetGroups)
                    sb.AppendLine($"{g.PiecesText}: {string.Join(g.Combine ? " + " : " / ", g.Options.Select(o => o.Name))}");
                foreach (var m in MainStats)
                    sb.AppendLine($"{m.Slot}: {string.Join(" / ", m.Stats)}");
                if (SubStats.Count > 0) sb.AppendLine(string.Join(", ", SubStats));
                if (Weapons.Count > 0) sb.AppendLine(string.Join(" / ", Weapons.Select(w => w.Name)));
            }
            return sb.ToString().TrimEnd();
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
                        IconUrl = catalog.IconUrl(IconKind.Skill, x.s.Icon),
                        Short = F(x.s.Short),
                        Description = F(x.s.Description),
                        DescriptionInEnglish = lookup != null && Untranslated(x.s.Description),
                        AiSummary = AiLine(record.Ai, x.i),
                    }).ToList(),
                Playstyle = PickAiText(record.Ai, t => t.Playstyle.Count > 0)?.Playstyle
                                .Select(GameTextFormatter.StripMarkup).ToList()
                            ?? (IReadOnlyList<string>)Array.Empty<string>(),
                Teams = Teams(record.Ai, catalog),
            };
            var image = record.Ai?.Image;
            if (image != null)
            {
                model.AmbienceUrl = image.Url;
                model.AmbienceAlt = GameTextFormatter.StripMarkup(image.Alt.Pick());
                model.AmbienceAspect = image.Width > 0 && image.Height > 0 ? (double)image.Width / image.Height : 0;
            }

            // Official data always wins; the AI build only fills a gap.
            var build = record.Build;
            if (build == null && record.Ai?.Build != null)
            {
                build = record.Ai.Build;
                model.BuildIsAi = true;
                model.BuildNote = GameTextFormatter.StripMarkup(
                    PickAiText(record.Ai, t => t.BuildNote.Length > 0)?.BuildNote ?? "");
            }
            if (build == null)
            {
                FillTips(model, record, catalog, T, F); // links, materials and AI picks exist without a build
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
            }).ToList();

            model.SubStats = build.SubStats.Select(Stat).ToList();

            model.Weapons = build.Weapons
                .Where(id => catalog.Weapons.ContainsKey(id))
                .Select(id => catalog.Weapons[id])
                .Select(w => new OverviewWeapon
                {
                    Name = T(w.Name),
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
            FillTips(model, record, catalog, T, F);
            return model;
        }

        private static void FillTips(CharacterOverviewModel model, CharacterRecord record, CharacterCatalog catalog,
                                     Func<string, string> T, Func<GameText, string> F)
        {
            var tips = new List<OverviewHighlight>();
            var priority = record.Ai?.Priority;
            // English: the name the game shows, so a reference matches what the player sees.
            string SkillName(int i) => i >= 0 && i < record.Skills.Count ? record.Skills[i].Name : "";

            if (model.HasBuild)
            {
                var g = model.SetGroups.FirstOrDefault();
                if (g != null && g.Options.Count > 0)
                    tips.Add(new OverviewHighlight
                    {
                        LabelKey = "Char_Tip_Set",
                        Text = g.Combine ? string.Join(" + ", g.Options.Select(o => o.Name)) : g.Options[0].Name,
                        IconUrl = g.Options[0].IconUrl,
                        IsAi = model.BuildIsAi,
                    });
                var primaries = model.MainStats.Select(m => m.Stats.FirstOrDefault()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (primaries.Count > 0)
                    tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_MainStats", Text = string.Join(" · ", primaries), IsAi = model.BuildIsAi });
                if (model.Weapons.Count > 0)
                    tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_Weapon", Text = model.Weapons[0].Name, IconUrl = model.Weapons[0].IconUrl, IsAi = model.BuildIsAi });
            }
            else if (priority != null)
            {
                var set = priority.Sets.Where(catalog.Sets.ContainsKey).Select(id => catalog.Sets[id]).FirstOrDefault();
                if (set != null)
                    tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_Set", Text = T(set.Name), IconUrl = catalog.IconUrl(IconKind.Set, set.Icon), IsAi = true });
                var weapon = priority.Weapons.Where(catalog.Weapons.ContainsKey).Select(id => catalog.Weapons[id]).FirstOrDefault();
                if (weapon != null)
                    tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_Weapon", Text = T(weapon.Name), IconUrl = catalog.IconUrl(IconKind.Weapon, weapon.Icon), IsAi = true });
            }

            // Leveling: the game's own order wins; the AI order only fills a gap.
            var leveling = record.Leveling.Select(SkillName).Where(n => n.Length > 0).ToList();
            bool levelingIsAi = false;
            if (leveling.Count == 0 && priority != null)
            {
                leveling = priority.Skills.Select(SkillName).Where(n => n.Length > 0).ToList();
                levelingIsAi = true;
            }
            if (leveling.Count > 0)
                tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_Leveling", Text = string.Join(" → ", leveling), IsAi = levelingIsAi });
            model.Leveling = leveling;
            model.LevelingIsAi = leveling.Count > 0 && levelingIsAi;

            var worth = priority?.Progression.Select(SkillName).Where(n => n.Length > 0).ToList();
            if (worth != null && worth.Count > 0)
                tips.Add(new OverviewHighlight { LabelKey = "Char_Tip_WorthIt", Text = string.Join(", ", worth), IsAi = true });

            model.Highlights = tips;

            IReadOnlyList<OverviewMaterial> Mats(IReadOnlyList<MaterialCost> list) => (list ?? Array.Empty<MaterialCost>())
                .Select(mc => new OverviewMaterial { Name = T(mc.Name), Count = mc.Count, IconUrl = catalog.IconUrl(IconKind.Material, mc.Icon) })
                .ToList();
            model.MaterialsAscension = Mats(record.Materials?.Ascension);
            model.MaterialsSkills = Mats(record.Materials?.Skills);

            model.Links = record.Links.Select(l => new OverviewLink
            {
                Site = l.Site,
                Label = l.Label,
                Url = l.Url,
                IconUrl = Uri.TryCreate(l.Url, UriKind.Absolute, out var u) ? $"https://{u.Host}/favicon.ico" : null,
            }).ToList();

            var guide = PickAiText(record.Ai, t => t.Guide.Count > 0)?.Guide ?? Array.Empty<GuideSection>();
            model.Guide = guide.Select(s => new OverviewGuideSection
            {
                Heading = GameTextFormatter.StripMarkup(s.Heading),
                Body = GameTextFormatter.StripMarkup(s.Body),
            }).ToList();

            string slug = SiteGameSlug(catalog.Game);
            if (slug.Length > 0 && record.Name.Length > 0)
                model.PageUrl = $"https://kaption.one/pl/postacie/{slug}/?q={Uri.EscapeDataString(record.Name)}";
        }

        /// <summary>The landing's per-game URL slug (landing/src/data/games.ts), not the wire id.</summary>
        internal static string SiteGameSlug(string wireId)
        {
            switch ((wireId ?? "").ToLowerInvariant())
            {
                case "genshin": return "genshin-impact";
                case "starrail": return "honkai-star-rail";
                case "zzz": return "zenless-zone-zero";
                default: return "";
            }
        }

        // Members missing from the roster are dropped; a team needs two left to be worth showing.
        private static IReadOnlyList<OverviewTeam> Teams(CharacterAi ai, CharacterCatalog catalog)
        {
            if (ai == null || ai.Teams.Count == 0) return Array.Empty<OverviewTeam>();
            int Order(string source) => source == "official" ? 0 : source == "ai" ? 1 : 2;
            return ai.Teams
                .Select(t => new OverviewTeam
                {
                    Name = GameTextFormatter.StripMarkup(t.Name.Pick()),
                    Source = t.Source,
                    Why = GameTextFormatter.StripMarkup(t.Why.Pick()),
                    Members = t.Members.Select(catalog.Find).Where(c => c != null).Select(c => new OverviewTeamMember
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Role = t.Roles.TryGetValue(c.Id, out var role) ? GameTextFormatter.StripMarkup(role.Pick()) : "",
                        IconUrl = catalog.IconUrl(IconKind.Character, c.Icon),
                    }).ToList(),
                })
                .Where(t => t.Members.Count >= 2)
                .OrderBy(t => Order(t.Source)) // stable: data order within a source
                .ToList();
        }

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
