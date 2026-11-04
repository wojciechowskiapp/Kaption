using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// detect.requireTitle: a game whose executable is generic (Wuthering Waves runs as Unreal's Client-Win64-Shipping)
    /// is that game only while one of its windows carries the game's title. Every detection path asks GameProcessMatch;
    /// these tests feed it made-up process and window lists, then check the real window enumeration once.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameProcessMatchTests
    {
        private const string Unreal = "Client-Win64-Shipping";

        /// <summary>Process <paramref name="pid"/> shows a visible window titled <paramref name="title"/>.</summary>
        private static GameProcessMatch.TopWindow Shows(uint pid, string title) => new GameProcessMatch.TopWindow(pid, title);
        private static GameDetection.RunningProcess Running(string name, uint pid) => new GameDetection.RunningProcess(name, pid);

        [TestMethod]
        public void TheShippedGames_AreRecognisedByTheirExecutableAlone()
        {
            foreach (string game in new[] { "Genshin", "StarRail", "ZZZ" })
                Assert.IsFalse(GameRegionProfile.Get(game).RequiresWindowTitle, game);
            var genshin = GameRegionProfile.Get("Genshin");
            Assert.IsTrue(GameProcessMatch.Matches(genshin, "GenshinImpact.exe", ""));
            Assert.IsTrue(GameProcessMatch.Matches(genshin, " yuanshen ", null));
            Assert.IsFalse(GameProcessMatch.Matches(genshin, Unreal, "Genshin Impact"));
            Assert.IsFalse(GameProcessMatch.Matches(null, "GenshinImpact", "Genshin Impact"));
            var pids = new uint[] { 4, 8 };
            CollectionAssert.AreEquivalent(pids, GameProcessMatch.GameProcesses(genshin, pids, Array.Empty<GameProcessMatch.TopWindow>()).ToArray(),
                "its own executable: every process the name found, no window needed");
        }

        [TestMethod]
        public void Matches_AGenericExecutable_NeedsTheTitleToo()
        {
            var p = UnrealProfile();
            Assert.IsTrue(GameProcessMatch.Matches(p, Unreal, "Fixture Game  "));
            Assert.IsTrue(GameProcessMatch.Matches(p, Unreal + ".exe", "fixture game"));
            Assert.IsFalse(GameProcessMatch.Matches(p, Unreal, "Some Other Unreal Game"), "another Unreal game");
            Assert.IsFalse(GameProcessMatch.Matches(p, Unreal, ""), "no title yet");
            Assert.IsFalse(GameProcessMatch.Matches(p, "chrome", "Fixture Game tier list - Google Chrome"), "a browser tab");
        }

        [TestMethod]
        public void GameProcesses_KeepOnlyTheProcessesShowingTheTitle()
        {
            var windows = new[]
            {
                Shows(10, "Some Other Unreal Game"),
                Shows(11, "Fixture Game"),
                Shows(12, ""),
                Shows(13, "UnrealSplash"), Shows(13, "Fixture Game"),
                Shows(99, "Fixture Game"), // not a process the name lookup found
            };
            CollectionAssert.AreEquivalent(new uint[] { 11, 13 },
                GameProcessMatch.GameProcesses(UnrealProfile(), new uint[] { 10, 11, 12, 13 }, windows).ToArray());
            Assert.AreEqual(0, GameProcessMatch.GameProcesses(UnrealProfile(), new uint[] { 10, 12 }, windows).Count);
            Assert.AreEqual(0, GameProcessMatch.GameProcesses(UnrealProfile(), null, windows).Count);
            Assert.AreEqual(0, GameProcessMatch.WithTitledWindow(Array.Empty<string>(), new uint[] { 11 }, windows).Count, "no titles, no match");
        }

        [TestMethod]
        public void RunningGame_AGenericExecutableCountsOnlyWithItsTitle()
        {
            using (GameRegistry.Use(GameRegistry.FromManifests(GameManifestLoader.LoadEmbedded().Append(UnrealFixture()))))
            {
                var unreal = new[] { Running(Unreal, 10), Running("explorer", 4) };
                Assert.IsNull(GameDetection.Match(unreal, new[] { Shows(10, "Some Other Unreal Game") }), "another Unreal game");
                Assert.IsNull(GameDetection.Match(unreal, Array.Empty<GameProcessMatch.TopWindow>()), "no window yet");
                Assert.AreEqual("FixtureGame", GameDetection.Match(unreal, new[] { Shows(10, "Fixture Game") }));

                var two = new[] { Running(Unreal, 10), Running(Unreal, 11) };
                Assert.AreEqual("FixtureGame", GameDetection.Match(two, new[] { Shows(10, "Other"), Shows(11, "Fixture Game") }),
                    "two Unreal games running: the titled one is found");

                var withGenshin = unreal.Append(Running("GenshinImpact", 20)).ToArray();
                Assert.AreEqual("Genshin", GameDetection.Match(withGenshin, new[] { Shows(10, "Fixture Game") }), "UI order still decides");
                Assert.AreEqual("Genshin", GameDetection.Match(new[] { Running("GenshinImpact", 20) }, Array.Empty<GameProcessMatch.TopWindow>()),
                    "a game with its own executable needs no window");
                Assert.IsNull(GameDetection.Match(Array.Empty<GameDetection.RunningProcess>()));
            }
        }

        [TestMethod]
        public void Foreground_AGenericExecutable_IsTheGameWithItsTitle_OrOnceTheStartGateCheckedIt()
        {
            var p = UnrealProfile();
            Assert.AreEqual(ForegroundTarget.Other, GameForegroundClassifier.Classify(200, 100, Unreal, false, "Some Other Unreal Game", p, false, false));
            Assert.AreEqual(ForegroundTarget.Game, GameForegroundClassifier.Classify(200, 100, Unreal, false, "Fixture Game", p, false, false));
            Assert.AreEqual(ForegroundTarget.Game, GameForegroundClassifier.Classify(200, 100, Unreal, true, "", p, false, false),
                "the process the start gate checked, showing another of its windows");
            Assert.AreEqual(ForegroundTarget.Other, GameForegroundClassifier.Classify(200, 100, "notepad", true, "Fixture Game", p, false, false),
                "a reused process id: the name it has now decides");
            Assert.AreEqual(ForegroundTarget.Game, GameForegroundClassifier.Classify(200, 100, "", true, "", p, false, false),
                "an elevated game whose name Windows won't give");
        }

        [TestMethod]
        public void TheRealWindowList_FindsThisProcessByItsWindowTitle()
        {
            RunSta(() =>
            {
                string title = "Kaption detection test " + Guid.NewGuid().ToString("N");
                // Opacity 0 and never activated, so nothing shows on the screen of whoever runs the tests.
                var window = new Window
                {
                    Title = title, WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0, ShowInTaskbar = false,
                    ShowActivated = false, Width = 120, Height = 80, Left = 0, Top = 0,
                };
                try
                {
                    window.Show();
                    DoEvents();
                    uint self = (uint)Environment.ProcessId;
                    CollectionAssert.AreEqual(new[] { self }, GameProcessMatch.WithTitledWindow(new[] { title }, new[] { self }).ToArray());
                    string unknown = "no window has this title " + Guid.NewGuid().ToString("N");
                    Assert.AreEqual(0, GameProcessMatch.WithTitledWindow(new[] { unknown }, new[] { self }).Count);

                    string name = Process.GetCurrentProcess().ProcessName;
                    var t0 = DateTime.UtcNow;
                    Assert.IsTrue(new GameProbe().Probe(new[] { name }, t0, new[] { title }).Running, "found by its process and its window");
                    Assert.IsFalse(new GameProbe().Probe(new[] { name }, t0, new[] { unknown }).Running, "the process alone is not enough");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>The GI-Test fixture game as a title-checked Unreal game, like Wuthering Waves.</summary>
        private static GameManifest UnrealFixture()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var m = GameManifestLoader.Parse(File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "games", "fixturegame.json")));
            m.Detect.ProcessNames = new[] { Unreal };
            m.Detect.WindowTitles = new[] { "Fixture Game" };
            m.Detect.RequireTitle = true;
            return m;
        }

        private static GameRegionProfile UnrealProfile() => GameProfileBuilder.Build(UnrealFixture());
    }
}
