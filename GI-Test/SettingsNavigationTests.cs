using System.Linq;
using System.Windows.Controls;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>Every 2.4 tab id and index still opens the page that holds its content now.</summary>
    [TestClass]
    public class SettingsNavigationTests
    {
        [TestMethod]
        [DataRow("Dashboard", "Dashboard", null)]
        [DataRow("Regions", "Game", "Region")]
        [DataRow("Appearance", "Appearance", null)]
        [DataRow("General", "Game", "Advanced")]
        [DataRow("Hotkeys", "Hotkeys", null)]
        [DataRow("Translations", "Game", "Packs")]
        [DataRow("Characters", "Characters", null)]
        [DataRow("Community", "Help", "Community")]
        [DataRow("Logs", "Help", "Log")]
        [DataRow("Home", "Dashboard", null)]
        [DataRow(" logs ", "Help", "Log")]
        public void OldTabIds_LandOnTheirNewPage(string id, string page, string section)
        {
            Assert.AreEqual((page, section), SettingsNavigation.Resolve(id));
        }

        [TestMethod]
        public void OldIndices_FollowThe24TabOrder()
        {
            string[] pages = Enumerable.Range(0, 9).Select(i => SettingsNavigation.ResolveLegacyIndex(i).Page).ToArray();
            CollectionAssert.AreEqual(new[] { "Dashboard", "Game", "Appearance", "Game", "Hotkeys", "Game", "Characters", "Help", "Help" }, pages);
            Assert.IsNull(SettingsNavigation.ResolveLegacyIndex(9).Page);
            Assert.IsNull(SettingsNavigation.ResolveLegacyIndex(-1).Page);
            Assert.IsNull(SettingsNavigation.Resolve("Nope").Page);
            Assert.IsNull(SettingsNavigation.Resolve(null).Page);
        }

        [TestMethod]
        public void TheRailHasTheSixPagesInOrder()
        {
            RunSta(() =>
            {
                LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window);
                var tabs = (TabControl)window.FindName("NavTabs");
                string[] tags = tabs.Items.OfType<TabItem>().Select(t => (string)t.Tag).ToArray();
                CollectionAssert.AreEqual(SettingsNavigation.Pages.ToArray(), tags);
            });
        }
    }
}
