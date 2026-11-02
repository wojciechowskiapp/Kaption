using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// "Gra też w drużynach": the reverse team index (who else's teams include a character), the teams v2 data it reads
    /// (roles as a list, release days) and the header's ambience crop (focal point).
    /// </summary>
    [TestClass]
    public class CharacterTeamIndexTests
    {
        internal const string Columbina = "10000125", Flins = "10000120", Ineffa = "10000116", Sucrose = "10000043",
            Nefer = "10000122", Lauma = "10000119", Nahida = "10000073", Bennett = "10000032", Xiangling = "10000023";

        internal static string Record(string id, string name, string day, string version, params string[] teams) =>
            "{ \"id\": \"" + id + "\", \"name\": \"" + name + "\", \"icon\": \"UI_AvatarIcon_" + id + "\", \"element\": \"Electro\", \"skills\": []" +
            (day != null ? ", \"release_date\": \"" + day + "\"" : "") +
            (version != null ? ", \"release_version\": \"" + version + "\"" : "") +
            (teams.Length > 0 ? ", \"ai\": { \"model\": \"m\", \"teams\": [" + string.Join(", ", teams) + "] }" : "") + " }";

        internal static string Team(string en, string pl, string source, string[] members, params (string Id, string En, string Pl)[] roles) =>
            "{ \"name\": { \"en\": \"" + en + "\", \"pl\": \"" + pl + "\" }, \"source\": \"" + source + "\", \"members\": [" +
            string.Join(", ", members.Select(m => "\"" + m + "\"")) + "], \"roles\": [" +
            string.Join(", ", roles.Select(r => "{ \"id\": \"" + r.Id + "\", \"role\": { \"en\": \"" + r.En + "\", \"pl\": \"" + r.Pl + "\" } }")) + "] }";

        internal static CharacterCatalog Catalog(params string[] records) =>
            CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(
                "{ \"schema\": 1, \"game\": \"genshin\", \"game_version\": \"7.2.0\", " +
                "\"icon_base\": { \"character\": \"https://img.example/avatar/\" }, \"icon_ext\": { \"character\": \".png\" }, " +
                "\"characters\": [" + string.Join(", ", records) + "] }")), "genshin");

        /// <summary>The owner's example: Columbina's team includes Flins, so Flins' page names Columbina's team.</summary>
        internal static CharacterCatalog Roster() => Catalog(
            Record(Columbina, "Columbina", "2025-12-24", "6.2",
                Team("Lunar Bloom", "Księżycowy rozkwit", "ai", new[] { Columbina, Flins, Ineffa, Sucrose },
                    (Flins, "Lunar-Charged damage", "Obrażenia Lunar-Charged"), (Columbina, "Hydro support", "Wsparcie Hydro"))),
            Record(Flins, "Flins", "2025-10-01", "6.1",
                Team("Lunar Electro", "Księżycowe Electro", "ai", new[] { Flins, Columbina, Ineffa, Sucrose },
                    (Columbina, "Hydro application", "Nakładanie Hydro"))),
            Record(Ineffa, "Ineffa", "2025-07-30", "5.8"),
            Record(Sucrose, "Sucrose", "2020-09-28", "1.0"),
            Record(Nefer, "Nefer", "2025-10-22", "6.1",
                Team("Lunar Bloom", "Księżycowy rozkwit", "ai", new[] { Nefer, Columbina, Lauma, Nahida },
                    (Columbina, "Hydro and healing", "Hydro i leczenie"))),
            Record(Lauma, "Lauma", "2025-09-10", "6.0",
                Team("Bloom", "Rozkwit", "ai", new[] { Lauma, Nefer, Columbina, Nahida, "99999999" })),
            Record(Nahida, "Nahida", "2022-11-02", "3.2"));

        [TestMethod]
        public void Flins_ShowsColumbinasTeam_WithFlinsRoleInIt()
        {
            var catalog = Roster();
            var m = CharacterOverviewBuilder.Build(catalog.Find(Flins), catalog, null);
            var host = m.TeamHosts.Single(h => h.HostId == Columbina);
            Assert.AreEqual("Columbina", host.HostName);
            Assert.AreEqual("Księżycowy rozkwit", host.TeamName, "the team's name, Polish first");
            Assert.AreEqual("Obrażenia Lunar-Charged", host.Role, "Flins' own role in Columbina's team (roles as a list)");
            Assert.AreEqual("https://img.example/avatar/UI_AvatarIcon_" + Columbina + ".png", host.IconUrl);
            Assert.IsTrue(new CharacterOverviewViewModel(m).HasTeams, "the Teams section shows for the reverse list alone");
        }

        [TestMethod]
        public void Index_IsSymmetric_WithEveryHostsTeams()
        {
            var catalog = Roster();
            foreach (var host in catalog.Characters)
            {
                var teams = CharacterTeamIndex.ShownTeams(host.Ai, catalog);
                foreach (string member in teams.SelectMany(t => t.Members).Distinct())
                {
                    if (member == host.Id || catalog.Find(member) == null) continue;
                    var entries = CharacterTeamIndex.HostsOf(catalog, member).Where(e => e.Host.Id == host.Id).ToList();
                    Assert.AreEqual(1, entries.Count, $"{catalog.Find(member).Name} lists {host.Name} once");
                    Assert.AreSame(teams.First(t => t.Members.Contains(member)), entries[0].Team, "with the host's first team that has them");
                }
            }
            foreach (var member in catalog.Characters)
                foreach (var entry in CharacterTeamIndex.HostsOf(catalog, member.Id))
                {
                    Assert.AreNotEqual(member.Id, entry.Host.Id, "never one's own team");
                    CollectionAssert.Contains(entry.Team.Members.ToList(), member.Id, $"{entry.Host.Name}'s team really has {member.Name}");
                }
            Assert.AreEqual(0, CharacterTeamIndex.HostsOf(catalog, "99999999").Count, "a member missing from the roster gets no page");
        }

        [TestMethod]
        public void Hosts_NewestReleaseFirst_UndatedLast()
        {
            var catalog = Roster();
            // Columbina appears in Flins' (6.1, Oct 1), Nefer's (6.1, Oct 22) and Lauma's (6.0) teams.
            CollectionAssert.AreEqual(new[] { "Nefer", "Flins", "Lauma" },
                CharacterTeamIndex.HostsOf(catalog, Columbina).Select(h => h.Host.Name).ToArray());

            var undated = Catalog(
                Record("1", "Old", "2021-01-05", "1.2", Team("A", "A", "ai", new[] { "1", "9" })),
                Record("2", "NoDay", null, null, Team("B", "B", "ai", new[] { "2", "9" })),
                Record("3", "SameDayNewerVersion", "2021-01-05", "1.3", Team("C", "C", "ai", new[] { "3", "9" })),
                Record("4", "SameDaySameVersionHigherId", "2021-01-05", "1.3", Team("D", "D", "ai", new[] { "4", "9" })),
                Record("9", "Member", "2020-01-01", "1.0"));
            CollectionAssert.AreEqual(new[] { "SameDaySameVersionHigherId", "SameDayNewerVersion", "Old", "NoDay" },
                CharacterTeamIndex.HostsOf(undated, "9").Select(h => h.Host.Name).ToArray());
        }

        [TestMethod]
        public void OneRowPerHost_TheTeamItsPageShowsFirst()
        {
            var catalog = Catalog(
                Record("1", "Host", "2024-01-01", "4.4",
                    Team("Second in data, AI", "Druga", "ai", new[] { "1", "2", "3" }),
                    Team("Official", "Oficjalna", "official", new[] { "1", "2" }),
                    Team("Too small", "Za mała", "official", new[] { "1", "404" })),
                Record("2", "Member", "2023-01-01", "4.0"),
                Record("3", "Other", "2023-01-01", "4.0"));
            var rows = CharacterTeamIndex.HostsOf(catalog, "2");
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("Oficjalna", rows[0].Team.Name.Pick(), "official teams come first on the host's page");
            Assert.AreEqual(0, rows[0].TeamIndex);
            Assert.AreEqual("Druga", CharacterTeamIndex.HostsOf(catalog, "3").Single().Team.Name.Pick());
        }

        [TestMethod]
        public void UpdaterPicks_ai_Plus_Labels_ShowNoSourceAndSortWithAi()
        {
            var catalog = Catalog(
                Record("1", "Host", null, null,
                    Team("Meta", "Meta", "meta-tool", new[] { "1", "2" }),
                    Team("Sourced", "Ze źródłami", "ai+sources", new[] { "1", "2" }),
                    Team("Game", "Gry", "official", new[] { "1", "2" })),
                Record("2", "Member", null, null));
            var vm = new CharacterOverviewViewModel(CharacterOverviewBuilder.Build(catalog.Find("1"), catalog, null));
            CollectionAssert.AreEqual(new[] { "official", "ai+sources", "meta-tool" }, vm.Teams.Select(t => t.Source).ToArray());
            Assert.IsTrue(vm.Teams[1].IsAi, "the updater's sourced pick reads as its own pick");
            Assert.IsFalse(vm.Teams[1].IsOtherSource, "so no \"Źródło: ai+sources\" label on the page");
            Assert.IsTrue(vm.Teams[2].IsOtherSource);
        }

        [TestMethod]
        public void Index_IsBuiltOncePerCatalog()
        {
            var catalog = Roster();
            Assert.AreSame(CharacterTeamIndex.HostsOf(catalog, Columbina), CharacterTeamIndex.HostsOf(catalog, Columbina));
            Assert.AreNotSame(CharacterTeamIndex.HostsOf(catalog, Columbina), CharacterTeamIndex.HostsOf(Roster(), Columbina),
                "a new bundle (a new catalog) brings a new index");
        }

        [TestMethod]
        public void Cap_SixHosts_ThenShowMoreRevealsTheRest()
        {
            var records = new List<string>();
            for (int i = 1; i <= 8; i++)
                records.Add(Record("2" + i, "Host " + i, $"2024-0{i}-01", "4." + i,
                    Team("Team " + i, "Drużyna " + i, "ai", new[] { "2" + i, Bennett }, (Bennett, "Healer", "Leczenie"))));
            records.Add(Record(Bennett, "Bennett", "2020-09-28", "1.0"));
            var catalog = Catalog(records.ToArray());
            var vm = new CharacterOverviewViewModel(CharacterOverviewBuilder.Build(catalog.Find(Bennett), catalog, null));

            Assert.AreEqual(8, vm.TeamHosts.Count);
            Assert.AreEqual(CharacterOverviewViewModel.TeamHostCap, vm.ShownTeamHosts.Count, "six until asked");
            CollectionAssert.AreEqual(new[] { "Host 8", "Host 7", "Host 6", "Host 5", "Host 4", "Host 3" },
                vm.ShownTeamHosts.Select(h => h.HostName).ToArray(), "the newest six");
            Assert.AreEqual(2, vm.HiddenTeamHosts);
            Assert.IsTrue(vm.HasHiddenTeamHosts);
            Assert.AreEqual("Leczenie", vm.ShownTeamHosts[0].Role);

            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            vm.ShowAllTeamHosts();
            Assert.AreEqual(8, vm.ShownTeamHosts.Count);
            Assert.AreEqual(0, vm.HiddenTeamHosts);
            Assert.IsFalse(vm.HasHiddenTeamHosts, "the button goes away");
            CollectionAssert.IsSubsetOf(new[] { "ShownTeamHosts", "HiddenTeamHosts", "HasHiddenTeamHosts" }, changed);

            var six = Catalog(records.Skip(2).ToArray());
            var exact = new CharacterOverviewViewModel(CharacterOverviewBuilder.Build(six.Find(Bennett), six, null));
            Assert.AreEqual(6, exact.ShownTeamHosts.Count);
            Assert.IsFalse(exact.HasHiddenTeamHosts, "exactly six: no button");
        }

        [TestMethod]
        public void Roles_ReadAsAMapOrAsAList()
        {
            var catalog = Catalog(
                Record("1", "A", null, null,
                    "{ \"name\": \"Map\", \"source\": \"ai\", \"members\": [\"1\", \"2\"], \"roles\": { \"2\": { \"en\": \"Shield\", \"pl\": \"Tarcza\" } } }",
                    "{ \"name\": \"List\", \"source\": \"ai\", \"members\": [\"1\", \"2\"], \"roles\": [ { \"id\": \"2\", \"role\": { \"en\": \"Buffer\" } }, { \"id\": \"\", \"role\": \"x\" }, 7 ] }"),
                Record("2", "B", null, null));
            var teams = catalog.Find("1").Ai.Teams;
            Assert.AreEqual("Tarcza", teams[0].Roles["2"].Pick());
            Assert.AreEqual("Buffer", teams[1].Roles["2"].Pick());
            Assert.AreEqual(1, teams[1].Roles.Count, "entries without an id or a role are skipped");
        }

        [TestMethod]
        public void Release_DayAndVersion_KeptOnlyWhenWellFormed()
        {
            var catalog = Catalog(Record("1", "A", "2025-12-24", "6.2"), Record("2", "B", "24.12.2025", "six"), Record("3", "C", null, "10.0.1"));
            Assert.AreEqual("2025-12-24", catalog.Find("1").ReleaseDate);
            Assert.AreEqual("6.2", catalog.Find("1").ReleaseVersion);
            Assert.AreEqual("", catalog.Find("2").ReleaseDate);
            Assert.AreEqual("", catalog.Find("2").ReleaseVersion);
            Assert.AreEqual("10.0.1", catalog.Find("3").ReleaseVersion);
        }

        [TestMethod]
        public void Ambience_FocalPoint_InRangeOnly_ElseALittleAboveTheMiddle()
        {
            static CharacterCatalog WithImage(string focal) => Catalog(
                "{ \"id\": \"1\", \"name\": \"A\", \"skills\": [], \"ai\": { \"model\": \"m\", \"image\": { \"url\": \"https://img.example/a.jpg\", " +
                "\"w\": 1216, \"h\": 688" + (focal != null ? ", \"focal\": " + focal : "") + " } } }");

            var good = WithImage("{ \"x\": 0.62, \"y\": 0.4 }");
            Assert.AreEqual(0.62, good.Find("1").Ai.Image.FocalX);
            var m = CharacterOverviewBuilder.Build(good.Find("1"), good, null);
            Assert.AreEqual(0.62, m.AmbienceFocalX, 1e-9);
            Assert.AreEqual(0.4, m.AmbienceFocalY, 1e-9);

            foreach (string bad in new[] { "{ \"x\": 2, \"y\": 0.5 }", "{ \"x\": 0.5 }", "{ \"x\": \"0.5\", \"y\": 0.5 }", "[0.5, 0.5]", null })
            {
                var c = WithImage(bad);
                Assert.IsNull(c.Find("1").Ai.Image.FocalX, bad ?? "none");
                var d = CharacterOverviewBuilder.Build(c.Find("1"), c, null);
                Assert.AreEqual(0.5, d.AmbienceFocalX);
                Assert.AreEqual(CharacterOverviewModel.DefaultFocalY, d.AmbienceFocalY);
            }
        }

        [TestMethod]
        public void HeroArtFrame_AStripOnTheFocalPoint_KeptInsideThePicture()
        {
            // A 980×200 header over a 1216×688 picture: the full width, a strip 36 % of its height.
            var strip = HeroArtFrame.Viewbox(980, 200, 1216, 688, new Point(0.5, 0.42));
            Assert.AreEqual(0, strip.X, 1e-9);
            Assert.AreEqual(1, strip.Width, 1e-9);
            Assert.AreEqual((1216.0 / 688) / (980.0 / 200), strip.Height, 1e-9);
            Assert.AreEqual(0.42, strip.Y + strip.Height / 2, 1e-9, "centred on the focal point");

            var top = HeroArtFrame.Viewbox(980, 200, 1216, 688, new Point(0.5, 0.02));
            Assert.AreEqual(0, top.Y, 1e-9, "never above the picture");
            var bottom = HeroArtFrame.Viewbox(980, 200, 1216, 688, new Point(0.5, 0.99));
            Assert.AreEqual(1, bottom.Bottom, 1e-9, "never below it");

            // A band narrower than the picture takes a column, centred on the focal point's x.
            var column = HeroArtFrame.Viewbox(300, 300, 1216, 688, new Point(0.8, 0.5));
            Assert.AreEqual(1, column.Height, 1e-9);
            Assert.AreEqual(688.0 / 1216, column.Width, 1e-9);
            Assert.AreEqual(1, column.Right, 1e-9, "clamped to the right edge");

            Assert.AreEqual(new Rect(0, 0, 1, 1), HeroArtFrame.Viewbox(0, 200, 1216, 688, new Point(0.5, 0.5)), "not laid out yet: the whole picture");
            Assert.AreEqual(new Rect(0, 0, 1, 1), HeroArtFrame.Viewbox(980, 200, 0, 0, new Point(0.5, 0.5)));
        }
    }
}
