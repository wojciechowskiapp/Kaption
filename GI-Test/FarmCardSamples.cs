using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Today;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Shared inputs for the farm card tests: the site's real schedule (landing/src/data/today/genshin.json), a small
    /// character catalog with the game's real ids, icons and books, and a showcase as the API returns it.
    /// </summary>
    internal static class FarmCardSamples
    {
        public static readonly DateTimeOffset Wednesday = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public static readonly DateTimeOffset Sunday = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

        public static string ScheduleJson()
        {
            string root = RenderTestKit.RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found.");
            return File.ReadAllText(Path.Combine(root, "landing", "src", "data", "today", "genshin.json"));
        }

        public static TodaySchedule Schedule() => TodaySchedule.Parse(ScheduleJson()) ?? throw new AssertFailedException("schedule parses");

        // (id, name, element, icon, books series, leveling positions into basic/skill/ultimate)
        private static readonly (string Id, string Name, string Element, string Icon, string Series, int[] Leveling)[] Characters =
        {
            ("10000052", "Raiden Shogun", "Electro", "UI_AvatarIcon_Shougun", "Light", new[] { 2, 1, 0 }),
            ("10000022", "Venti", "Anemo", "UI_AvatarIcon_Venti", "Ballad", new[] { 2, 1 }),
            ("10000030", "Zhongli", "Geo", "UI_AvatarIcon_Zhongli", "Gold", new[] { 1, 2 }),
            ("10000025", "Xingqiu", "Hydro", "UI_AvatarIcon_Xingqiu", "Gold", new[] { 2, 1 }),
            ("10000070", "Nilou", "Hydro", "UI_AvatarIcon_Nilou", "Praxis", new[] { 1, 2 }),
            ("10000047", "Kaedehara Kazuha", "Anemo", "UI_AvatarIcon_Kazuha", "Diligence", new[] { 1, 2 }),
            ("10000032", "Bennett", "Pyro", "UI_AvatarIcon_Bennett", "Resistance", new[] { 2, 1 }),
            ("10000089", "Furina", "Hydro", "UI_AvatarIcon_Furina", "Justice", new[] { 1, 2 }),
        };

        public static CharacterCatalog Catalog()
        {
            var schedule = JsonDocument.Parse(ScheduleJson()).RootElement;
            int IdOf(string itemName) => schedule.GetProperty("domains").EnumerateArray()
                .SelectMany(d => d.GetProperty("items").EnumerateArray())
                .Where(i => i.GetProperty("name").GetString() == itemName).Select(i => i.GetProperty("id").GetInt32()).FirstOrDefault();
            var chars = Characters.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                element = c.Element,
                icon = c.Icon,
                rarity = 5,
                skills = new[] { new { kind = "basic", name = "Normal Attack" }, new { kind = "skill", name = "Skill" }, new { kind = "ultimate", name = "Burst" } },
                leveling = c.Leveling,
                materials = new
                {
                    skills = new[] { "Teachings of", "Guide to", "Philosophies of" }
                        .Select(p => $"{p} {c.Series}").Select(n => new { name = n, count = 1, icon = $"UI_ItemIcon_{IdOf(n)}" })
                        .Append(new { name = "Crown of Insight", count = 3, icon = "UI_ItemIcon_104319" }).ToArray(),
                },
            });
            string json = JsonSerializer.Serialize(new
            {
                schema = 1,
                game = "genshin",
                game_version = "7.1.0",
                icon_base = new { character = "https://enka.network/ui/", material = "https://gi.yatta.moe/assets/UI/" },
                icon_ext = new { character = ".png", material = ".png" },
                characters = chars,
            });
            var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "genshin");
            Assert.AreEqual(Characters.Length, catalog.Characters.Count, "the sample catalog loads");
            return catalog;
        }

        /// <summary>A roster as GET /api/cards/genshin/&lt;uid&gt; answers, trimmed to the fields the card reads.</summary>
        public static string RosterJson(bool empty = false)
        {
            object T(string kind, string s, int level, int bonus = 0) => new { kind, label = new { en = kind, pl = kind }, @short = s, level, bonus, icon = (string)null };
            object C(string id, string name, string el, int level, params object[] talents) =>
                new { id, name = new { en = name, pl = name }, element = new { en = el, pl = el }, elementKey = el, level, image = new { icon = (string)null, art = (string)null }, talents };
            var characters = empty ? Array.Empty<object>() : new[]
            {
                C("10000052", "Raiden Shogun", "Electro", 90, T("basic", "NA", 6), T("skill", "E", 8), T("ultimate", "Q", 9, 3)),
                C("10000022", "Venti", "Anemo", 90, T("basic", "NA", 1), T("skill", "E", 6), T("ultimate", "Q", 6, 3)),
                C("10000030", "Zhongli", "Geo", 80, T("basic", "NA", 1), T("skill", "E", 9), T("ultimate", "Q", 8)),
                C("10000025", "Xingqiu", "Hydro", 80, T("basic", "NA", 1), T("skill", "E", 6), T("ultimate", "Q", 9, 3)),
                C("10000070", "Nilou", "Hydro", 90, T("basic", "NA", 1), T("skill", "E", 4), T("ultimate", "Q", 6)),
                C("10000047", "Kaedehara Kazuha", "Anemo", 90, T("basic", "NA", 1), T("skill", "E", 9), T("ultimate", "Q", 9)),
                C("10000032", "Bennett", "Pyro", 80, T("basic", "NA", 1), T("skill", "E", 5), T("ultimate", "Q", 10, 3)),
                C("10000089", "Furina", "Hydro", 90, T("basic", "NA", 1), T("skill", "E", 9), T("ultimate", "Q", 8)),
            };
            return JsonSerializer.Serialize(new { schema = 2, game = "genshin", uid = "700000001", showcase = empty ? "hidden" : "ok", characters });
        }

        public static ShowcaseRoster Roster(DateTimeOffset now, bool empty = false) =>
            ShowcaseRoster.FromApi(RosterJson(empty), "700000001", ServerDay.At(now, FarmServer.Europe).Key(FarmServer.Europe), now);

        public static FarmCardModel Model(DateTimeOffset now, bool hasUid, bool emptyShowcase = false, string lang = "pl-PL", bool unknownUid = false) =>
            FarmCardBuilder.Build(Schedule(), ServerDay.At(now, FarmServer.Europe), now, hasUid, hasUid && !unknownUid ? Roster(now, emptyShowcase) : null,
                Catalog(), lang, unknownUid);
    }
}
