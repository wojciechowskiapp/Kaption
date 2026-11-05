using System;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Games;
using System.Windows.Controls;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// A beta game whose first pack the server does not list yet: the app says the subtitles are on the way (Start card,
    /// start gate, setup window) instead of telling players to download a pack that does not exist. A live game with
    /// nothing listed, or a beta game before any listing, keeps the old wording. WuWa is live since session 59; the beta
    /// path is still checked on it, marked beta for the test (<see cref="WuWaAsBeta"/>).
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class PackComingTests
    {
        [TestInitialize]
        public void Reset() => PackAvailability.Reset();

        [TestCleanup]
        public void Cleanup() => PackAvailability.Reset();

        /// <summary>The embedded games with WuWa back at beta, until disposed.</summary>
        private static IDisposable WuWaAsBeta()
        {
            var manifests = GameManifestLoader.LoadEmbedded().ToList();
            manifests.Single(m => m.Id == "wuwa").Status = GameManifest.StatusBeta;
            return GameRegistry.Use(GameRegistry.FromManifests(manifests));
        }

        [TestMethod]
        public void WuWaIsLive_SoAnEmptyListingIsNotComing()
        {
            Assert.IsFalse(GameRegistry.IsBeta("WuWa"), "games/wuwa.json is live");
            PackAvailability.Record("wuwa", "pl", 0);
            Assert.IsFalse(PackAvailability.IsComing("WuWa", "PL"), "a live game with nothing listed is a real problem");
        }

        [TestMethod]
        public void ComingIsABetaGame_WithAnEmptyListing()
        {
            using (WuWaAsBeta())
            {
                Assert.IsFalse(PackAvailability.IsComing("WuWa", "PL"), "nothing listed yet: unknown, not coming");
                PackAvailability.Record("wuwa", "pl", 0);
                Assert.IsTrue(PackAvailability.IsComing("WuWa", "PL"), "wire id and GameId, any case, are one game");
                Assert.IsTrue(PackAvailability.IsComing("Wuthering Waves", "pl"), "an alias too");
                Assert.IsFalse(PackAvailability.IsComing("WuWa", "EN"), "per language");
                PackAvailability.Record("WuWa", "PL", 2);
                Assert.IsFalse(PackAvailability.IsComing("WuWa", "PL"), "the first pack is out");

                PackAvailability.Record("genshin", "pl", 0);
                Assert.IsFalse(PackAvailability.IsComing("Genshin", "PL"), "a live game with nothing listed is a real problem");
                Assert.IsFalse(PackAvailability.IsComing(null, "PL"));
                Assert.IsFalse(PackAvailability.IsComing("WuWa", ""));
            }
        }

        [TestMethod]
        public void Changed_FiresWhenTheAnswerCanChange_Only()
        {
            using (WuWaAsBeta())
            {
                int fired = 0;
                void Count() => fired++;
                PackAvailability.Changed += Count;
                try
                {
                    PackAvailability.Record("wuwa", "pl", 0);
                    PackAvailability.Record("wuwa", "pl", 0);
                    PackAvailability.Record("wuwa", "pl", 3);
                    PackAvailability.Record("wuwa", "pl", 4);
                    Assert.AreEqual(2, fired, "first listing, then empty to listed");
                }
                finally { PackAvailability.Changed -= Count; }
            }
        }

        [TestMethod]
        public void NoWarningDialog_ForABetaGameWithNothingPublished()
        {
            using (WuWaAsBeta())
            {
                var nothing = new DictionaryInventoryResult();
                Assert.IsNull(DictionaryInventoryService.ExplainMissingPack(nothing, "WuWa", "PL", isSignedIn: true, remoteCatalogOk: true),
                    "the Start card says the pack is on the way");
                StringAssert.Contains(DictionaryInventoryService.ExplainMissingPack(nothing, "Genshin", "PL", true, true), "hasn't published one",
                    "a live game keeps the honest warning");
                StringAssert.Contains(DictionaryInventoryService.ExplainMissingPack(nothing, "WuWa", "PL", true, false), "couldn't reach the server",
                    "offline is still offline");
            }
        }

        [TestMethod]
        public void TheStartCard_SaysOnTheWay_InPolish_WithTheGamesName()
        {
            RunSta(() =>
            {
                var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, "Dark");
                string L(string k, string f) => host.TryFindResource(k) as string ?? f;
                var inputs = new StartCardInputs { PackMissing = true, PackComing = true, Reader = ReaderState.Ready, GameName = "Wuthering Waves" };
                var state = StartCardModel.Compute(inputs);
                SettingsWindow.ApplyStartCardTo(window.FindName, state, inputs, L, CultureInfo.GetCultureInfo("pl-PL"));
                SettingsWindow.ApplyStateChipTo(window.FindName, state, L);
                Assert.AreEqual("Napisy do tej gry są w drodze", ((TextBlock)window.FindName("StateCardTitle")).Text);
                Assert.AreEqual("Wuthering Waves to nowość w Kaption. Gdy napisy będą gotowe, Kaption pobierze je sam przy następnym uruchomieniu.",
                    ((TextBlock)window.FindName("StateCardDetail")).Text);
                Assert.AreEqual("Wkrótce", ((TextBlock)window.FindName("DashOcrDetailStatus")).Text);
                Assert.AreEqual(System.Windows.Visibility.Collapsed, ((Button)window.FindName("DashToggleOcrButton")).Visibility,
                    "no button that asks for a download");
            });
        }
    }
}
