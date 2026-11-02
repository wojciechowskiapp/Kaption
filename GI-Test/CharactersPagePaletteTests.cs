using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The Characters page inside the 2.5 shell: its colours follow the app palette (Dark, Light, Auto, high contrast)
    /// whatever hosts it, and it relies on the host for the shared control styles.
    /// </summary>
    [TestClass]
    public class CharactersPagePaletteTests
    {
        private static string CharacterPalette(CharactersPage page) =>
            page.Resources.MergedDictionaries.Select(d => d.Source?.OriginalString ?? "")
                .FirstOrDefault(s => s.Contains("CharacterPalette")) ?? "";

        [TestMethod]
        public void FollowsTheHostPalette_AndTheThemeSwitch()
        {
            RenderTestKit.RunSta(() =>
            {
                var host = new Border();
                host.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Themes/Palette.Dark.xaml"));
                host.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Resources/ModernStyles.xaml"));
                foreach (var d in RenderTestKit.Strings("pl-PL")) host.Resources.MergedDictionaries.Add(d);

                var page = new CharactersPage { FetchImages = false };
                host.Child = page;
                StringAssert.EndsWith(CharacterPalette(page), "CharacterPaletteDark.xaml", "a dark host gets the dark page, with no call from the host");

                // ThemeService swaps the palette slot in place, then the shell asks the page to follow.
                host.Resources.MergedDictionaries[0] = RenderTestKit.Dictionary("Themes/Palette.Light.xaml");
                page.SyncPaletteWithApp();
                StringAssert.EndsWith(CharacterPalette(page), "CharacterPaletteLight.xaml");
            });
        }

        [TestMethod]
        public void TheResolvedColourDecides_HighContrastIncluded()
        {
            Assert.IsTrue(CharactersPage.IsDarkSurface(Colors.Black), "high contrast black");
            Assert.IsTrue(CharactersPage.IsDarkSurface(Color.FromRgb(0x0B, 0x0D, 0x17)), "ink canvas");
            Assert.IsFalse(CharactersPage.IsDarkSurface(Colors.White), "high contrast white");
            Assert.IsFalse(CharactersPage.IsDarkSurface(Color.FromRgb(0xF7, 0xF8, 0xFC)), "light canvas");
        }

        [TestMethod]
        public void NoPrivateCopyOfTheSharedStyles()
        {
            RenderTestKit.RunSta(() =>
            {
                var page = new CharactersPage { FetchImages = false };
                Assert.IsFalse(page.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.Contains("ModernStyles") == true),
                    "Settings merges ModernStyles.xaml; a copy inside the page duplicates every style and shadows the app's");
            });
        }
    }
}
