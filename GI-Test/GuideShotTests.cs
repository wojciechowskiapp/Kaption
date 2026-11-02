using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The 2.5.0 guide and release-announcement screenshots at 1280×720: the six rail pages of Settings (Postacie with
    /// a real character) and the five setup steps, in Polish and English, dark and light. The setup window is a fixed
    /// 1000×720, so its steps are centred on a 1280×720 frame. Written to %TEMP%\kaption-w15\guides\720p\.
    /// </summary>
    [TestClass]
    public class GuideShotTests
    {
        private const double Width = 1280, Height = 720;
        public static string Dir => Path.Combine(ShellRenderTests.GuidesDir, "720p");

        [TestMethod]
        public void RendersTheSixRailPagesAt720p()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var catalog = CharacterOverviewRenderTests.LoadCatalog(root, "genshin", injectAiFor: "Chiori"); // the AI block a live bundle carries
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    int n = 0;
                    foreach (string page in SettingsNavigation.Pages)
                    {
                        n++;
                        Select(window, page);
                        bool characters = page == SettingsNavigation.Characters;
                        if (characters && catalog != null) ShowCharacter(window, catalog, "Chiori");
                        Layout(host, Width, Height);
                        if (characters && window.FindName("CharactersPage") is CharactersPage cp
                            && cp.FindName("CharacterList") is ListBox list && list.SelectedItem != null)
                        {
                            list.ScrollIntoView(list.SelectedItem); // needs a laid-out list
                            Layout(host, Width, Height);
                        }
                        var shot = Snapshot(host);
                        Assert.AreEqual((int)Width, shot.PixelWidth);
                        Assert.AreEqual((int)Height, shot.PixelHeight);
                        written.Add(Write(shot, $"page-{n}-{page.ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                    }
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheSetupStepsFramedAt720p()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                foreach (OnboardingStep step in OnboardingFlow.Steps)
                {
                    var host = LoadLooseWindow("Views/OnboardingWindow.xaml", lang, out var window, palette);
                    OnboardingTests.Fill(host, window, step, lang);
                    Layout(host, 1000, 720);
                    var framed = Frame(Snapshot(host), palette == "Dark");
                    string name = $"setup-{(int)step + 1}-{step.ToString().ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                    written.Add(Write(framed, name));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static void Select(Window window, string page)
        {
            var tabs = (TabControl)window.FindName("NavTabs");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (string)t.Tag == page);
        }

        // The page as the app fills it: the Genshin catalog, the Polish pack's names, cached art; no downloads.
        private static void ShowCharacter(Window window, CharacterCatalog catalog, string name)
        {
            var page = (CharactersPage)window.FindName("CharactersPage");
            page.GameSource = () => "Genshin";
            page.PackSource = () => null;
            page.PackReady = () => true;
            page.FetchImages = false;
            page.LookupOverride = CharacterOverviewRenderTests.PackLookup.For("genshin", catalog);
            var load = page.LoadAsync("Genshin", _ => Task.FromResult(catalog));
            CharacterOverviewRenderTests.Pump(() => load.IsCompleted);
            var list = (ListBox)page.FindName("CharacterList");
            var item = list.Items.Cast<CharacterListItem>().FirstOrDefault(i => i.Name == name);
            if (item == null) return;
            list.SelectedItem = item;
            var overview = (ContentControl)page.FindName("OverviewHost");
            CharacterOverviewRenderTests.Pump(() => (overview.Content as FrameworkElement)?.DataContext is CharacterOverviewViewModel vm && vm.Name == name);
        }

        // The setup window does not resize: centred on a 1280×720 frame, a hairline at its sides.
        private static BitmapSource Frame(BitmapSource window, bool dark)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var backdrop = dark ? Color.FromRgb(0x05, 0x06, 0x0B) : Color.FromRgb(0xE4, 0xE7, 0xED);
                dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, Width, Height));
                double x = Math.Round((Width - window.PixelWidth) / 2);
                dc.DrawImage(window, new Rect(x, 0, window.PixelWidth, Math.Min(Height, window.PixelHeight)));
                var hairline = new Pen(new SolidColorBrush(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)), 1);
                dc.DrawLine(hairline, new Point(x - 0.5, 0), new Point(x - 0.5, Height));
                dc.DrawLine(hairline, new Point(x + window.PixelWidth + 0.5, 0), new Point(x + window.PixelWidth + 0.5, Height));
            }
            var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static string Write(BitmapSource bitmap, string name)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
    }
}
