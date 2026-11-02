using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The section tab row when it is wider than the page: chevrons, edge fades, the active tab in view.</summary>
    [TestClass]
    public class CharacterTabStripTests
    {
        [TestMethod]
        public void Overflow_ReportsTheSidesThatScroll()
        {
            RenderTestKit.RunSta(() =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                for (int i = 0; i < 10; i++) row.Children.Add(new Border { Width = 100, Height = 20 });
                var sv = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = row,
                };
                HorizontalOverflow.SetIsEnabled(sv, true);
                RenderTestKit.Layout(sv, 300, 20);
                HorizontalOverflow.Refresh(sv);
                Assert.IsFalse(HorizontalOverflow.GetCanScrollLeft(sv));
                Assert.IsTrue(HorizontalOverflow.GetCanScrollRight(sv));
                Assert.IsNotNull(sv.OpacityMask, "the right edge fades");

                HorizontalOverflow.ScrollIntoView(sv, (FrameworkElement)row.Children[9]);
                sv.UpdateLayout();
                HorizontalOverflow.Refresh(sv);
                Assert.IsTrue(HorizontalOverflow.GetCanScrollLeft(sv));
                Assert.IsFalse(HorizontalOverflow.GetCanScrollRight(sv), "the last item is in view: the end");

                HorizontalOverflow.Page(sv, -1);
                sv.UpdateLayout();
                HorizontalOverflow.Refresh(sv);
                Assert.IsTrue(HorizontalOverflow.GetCanScrollRight(sv), "a chevron press goes back");

                // A row that fits: no chevrons, no fade.
                var fits = new ScrollViewer { Content = new Border { Width = 120, Height = 20 },
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
                HorizontalOverflow.SetIsEnabled(fits, true);
                RenderTestKit.Layout(fits, 300, 20);
                HorizontalOverflow.Refresh(fits);
                Assert.IsFalse(HorizontalOverflow.GetCanScrollLeft(fits) || HorizontalOverflow.GetCanScrollRight(fits));
                Assert.IsNull(fits.OpacityMask);
            });
        }

        [TestMethod]
        public void NarrowPage_ShowsChevrons_AndKeepsTheActiveTabInView()
        {
            RenderTestKit.RunSta(() =>
            {
                string root = RenderTestKit.RepoRoot();
                string dir = Path.Combine(root, "tools", "out", "genshin");
                string file = Directory.Exists(dir) ? Directory.GetFiles(dir, "characters-*.json").OrderByDescending(f => f).FirstOrDefault() : null;
                if (file == null) Assert.Inconclusive("No tools/out/genshin/characters-*.json found.");
                var catalog = CharacterCatalog.Load(new MemoryStream(File.ReadAllBytes(file)), "genshin");
                var model = CharacterOverviewBuilder.Build(catalog.Characters.First(c => c.Name == "Chiori"), catalog, null);

                var view = new CharacterOverviewView(model);
                var window = new Window { Width = 360, Height = 720, Content = view, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                window.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Resources/CharacterPaletteDark.xaml"));
                window.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Resources/CharacterLayoutPopup.xaml"));
                foreach (var d in RenderTestKit.Strings("pl-PL")) window.Resources.MergedDictionaries.Add(d);
                window.Show();
                try
                {
                    for (int i = 0; i < 3; i++) { window.UpdateLayout(); RenderTestKit.DoEvents(); }
                    var row = Descendants<ScrollViewer>(view.InlineTabs).Single(s => s.Name == "TabsRow");
                    var back = Descendants<Button>(view.InlineTabs).Single(b => b.Name == "TabsBack");
                    var forward = Descendants<Button>(view.InlineTabs).Single(b => b.Name == "TabsForward");
                    Assert.IsTrue(row.ScrollableWidth > 0, "the popup width cannot hold every tab");
                    Assert.AreEqual(Visibility.Collapsed, back.Visibility, "nothing to the left yet");
                    Assert.AreEqual(Visibility.Visible, forward.Visibility);

                    Assert.IsTrue(view.ScrollToSection("Links"));
                    for (int i = 0; i < 3; i++) { window.UpdateLayout(); RenderTestKit.DoEvents(); }
                    var tab = Descendants<RadioButton>(row).Single(r => (string)r.Tag == "Links");
                    double x = tab.TransformToAncestor(row).Transform(new Point(0, 0)).X;
                    Assert.IsTrue(x >= 0 && x + tab.ActualWidth <= row.ViewportWidth + 0.5,
                        $"the active tab shows whole (x={x:0.#}, w={tab.ActualWidth:0.#}, viewport={row.ViewportWidth:0.#})");
                    Assert.AreEqual(Visibility.Visible, back.Visibility, "scrolled: the way back appears");
                    Assert.IsTrue(tab.IsChecked == true);
                    Assert.IsTrue(Descendants<TextBlock>(row).All(t => t.TextTrimming == TextTrimming.None), "labels are never trimmed");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [TestMethod]
        public void RedrawnPage_KeepsTheReadingPosition()
        {
            // The Characters tab redraws the page when images arrive; the new view must not clamp a deep offset to the
            // height of its first (eager) sections.
            RenderTestKit.RunSta(() =>
            {
                string dir = Path.Combine(RenderTestKit.RepoRoot(), "tools", "out", "genshin");
                string file = Directory.Exists(dir) ? Directory.GetFiles(dir, "characters-*.json").OrderByDescending(f => f).FirstOrDefault() : null;
                if (file == null) Assert.Inconclusive("No tools/out/genshin/characters-*.json found.");
                var catalog = CharacterCatalog.Load(new MemoryStream(File.ReadAllBytes(file)), "genshin");
                var model = CharacterOverviewBuilder.Build(catalog.Characters.First(c => c.Name == "Bennett"), catalog, null);

                var host = new ContentControl();
                var window = new Window { Width = 900, Height = 700, Content = host, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                window.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Resources/CharacterPaletteLight.xaml"));
                window.Resources.MergedDictionaries.Add(RenderTestKit.Dictionary("Resources/CharacterLayoutPage.xaml"));
                foreach (var d in RenderTestKit.Strings("pl-PL")) window.Resources.MergedDictionaries.Add(d);
                window.Show();
                try
                {
                    var first = new CharacterOverviewView(model);
                    host.Content = first;
                    for (int i = 0; i < 3; i++) { window.UpdateLayout(); RenderTestKit.DoEvents(); }
                    Assert.IsTrue(first.ScrollToSection("Ranks"));
                    window.UpdateLayout();
                    double offset = first.VerticalOffset;
                    Assert.IsTrue(offset > 600, $"the ladder is deep in the page ({offset:0})");

                    var redrawn = new CharacterOverviewView(model);
                    host.Content = redrawn;
                    redrawn.Dispatcher.Invoke(() => redrawn.ScrollTo(offset), System.Windows.Threading.DispatcherPriority.Loaded);
                    window.UpdateLayout();
                    Assert.AreEqual(offset, redrawn.VerticalOffset, 1.0, "same reading position after the redraw");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var deeper in Descendants<T>(child)) yield return deeper;
            }
        }
    }
}
