using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Renders the app's windows to PNG from their XAML (no code-behind), in both palettes and at 100 % and 125 %,
    /// for visual review; also proves every window parses with the current resources.
    /// Output: %TEMP%\kaption-render\window-*.png.
    /// </summary>
    [TestClass]
    public class WindowRenderTests
    {
        // The Settings window's default size (no native frame: the whole window is client area).
        private const double SettingsWidth = 1180, SettingsHeight = 780;
        public static readonly string[] Palettes = { "Dark", "Light" };

        [TestMethod]
        public void RendersEverySettingsPage()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    var tabs = (TabControl)window.FindName("NavTabs");
                    Assert.AreEqual(SettingsNavigation.Pages.Count, tabs.Items.Count, "one rail item per page");
                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        var tab = (TabItem)tabs.Items[i];
                        Assert.AreEqual(SettingsNavigation.Pages[i], tab.Tag as string, "rail order = SettingsNavigation.Pages");
                        if (lang == "en-US" && i > 0) break; // English: Start is enough to check fit
                        tabs.SelectedIndex = i;
                        Layout(host, SettingsWidth, SettingsHeight);
                        string name = $"window-settings-{i}-{Tag(tab)}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}";
                        written.Add(Save(Snapshot(host), name + ".png"));
                        if (lang == "pl-PL") written.Add(Save(Snapshot(host, 1.25), name + "@125.png"));
                    }
                    // Narrow window: icon-only rail.
                    tabs.SelectedIndex = 0;
                    NavRail.SetIsCompact(tabs, true);
                    Layout(host, 960, 640);
                    written.Add(Save(Snapshot(host), $"window-settings-compact-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersTheSmallWindows()
        {
            var windows = new (string View, double Width, double Height)[]
            {
                ("Views/ModernDialog.xaml", 460, double.NaN),
                ("Views/LoginWindow.xaml", 480, 560),
                ("Views/EulaAcceptanceWindow.xaml", 560, 580),
                ("Views/SendFeedbackWindow.xaml", 520, 520),
                ("Views/FileProtectionBootstrapWindow.xaml", 420, 220),
                ("Views/ReferralsWindow.xaml", 560, 640),
            };
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in Palettes)
                foreach (var (view, width, height) in windows)
                {
                    var host = LoadLooseWindow(view, "pl-PL", out _, palette);
                    if (host.Background is SolidColorBrush b && b.Color.A == 0)
                        host.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B)); // transparent windows
                    Layout(host, width, height);
                    string name = view.Substring(view.LastIndexOf('/') + 1).Replace(".xaml", "");
                    written.Add(Save(Snapshot(host), $"window-{name}-pl-{palette.ToLowerInvariant()}.png"));
                    written.Add(Save(Snapshot(host, 1.25), $"window-{name}-pl-{palette.ToLowerInvariant()}@125.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static string Tag(TabItem tab) => (tab.Tag as string ?? "tab").ToLowerInvariant();
    }
}
