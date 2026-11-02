using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Renders the app's windows to PNG from their XAML (no code-behind), one image per tab or wizard step,
    /// for visual review. Also proves every window parses with the current resources.
    /// Output: %TEMP%\kaption-render\window-*.png.
    /// </summary>
    [TestClass]
    public class WindowRenderTests
    {
        // Client area of a 820×760 window: the frame takes about 16 px across and 39 px down.
        private const double SettingsWidth = 804, SettingsHeight = 721;

        [TestMethod]
        public void RendersEverySettingsTab()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out _);
                    var tabs = Logical<TabControl>(host).Single();
                    for (int i = 0; i < tabs.Items.Count; i++)
                    {
                        var tab = (TabItem)tabs.Items[i];
                        if (lang == "en-US" && i > 0) break; // English: the Dashboard is enough to check fit
                        tabs.SelectedIndex = i;
                        Layout(host, SettingsWidth, SettingsHeight);
                        written.Add(Save(Snapshot(host), $"window-settings-{i}-{Tag(tab)}-{lang.Substring(0, 2)}.png"));
                    }
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void RendersEverySetupWizardStep()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var host = LoadLooseWindow("Views/SetupWizardWindow.xaml", "pl-PL", out var window);
                var steps = Enumerable.Range(1, 4).Select(n => (FrameworkElement)window.FindName($"Step{n}Panel")).ToList();
                for (int n = 0; n < steps.Count; n++)
                {
                    for (int k = 0; k < steps.Count; k++)
                        steps[k].Visibility = k == n ? Visibility.Visible : Visibility.Collapsed;
                    Layout(host, 784, 641);
                    written.Add(Save(Snapshot(host), $"window-wizard-step{n + 1}-pl.png"));
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
                ("Views/LoginWindow.xaml", 464, 521),
                ("Views/EulaAcceptanceWindow.xaml", 504, 421),
                ("Views/SendFeedbackWindow.xaml", 504, 481),
                ("Views/FullscreenTipWindow.xaml", 500, double.NaN),
                ("Views/FileProtectionBootstrapWindow.xaml", 404, 181),
            };
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (var (view, width, height) in windows)
                {
                    var host = LoadLooseWindow(view, "pl-PL", out _);
                    if (host.Background is SolidColorBrush b && b.Color.A == 0)
                        host.Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x5F, 0x6B)); // transparent windows
                    Layout(host, width, height);
                    string name = view.Substring(view.LastIndexOf('/') + 1).Replace(".xaml", "");
                    written.Add(Save(Snapshot(host), $"window-{name}-pl.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static string Tag(TabItem tab) => (tab.Tag as string ?? "tab").ToLowerInvariant();

        private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (!(child is DependencyObject d)) continue;
                if (d is T t) yield return t;
                foreach (var x in Logical<T>(d)) yield return x;
            }
        }
    }
}
