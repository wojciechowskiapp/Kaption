using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The "Fix this line" window (Ctrl+Shift+E) as the community pages on kaption.one show it: dark palette, Polish and
    /// English interface, the hub's example line (Paimon, "Let's head into town.", "Chodźmy do miasto." with the player's
    /// "Chodźmy do miasta." typed in), drawn at 200 % (the 560 px window is 1120 px wide). A publishing render, not a check:
    /// it runs only with KAPTION_RENDER_COMMUNITY=1 and writes landing/src/assets/community/fix-window-{pl,en}.png
    /// (FIX_WINDOW_IMAGE in landing/src/data/community.ts), or KAPTION_COMMUNITY_DIR when set.
    /// </summary>
    [TestClass]
    public class CommunityFixWindowShotTests
    {
        private const double Width = 560, Scale = 2.0;
        private const string English = "Let's head into town.";
        private const string Current = "Chodźmy do miasto.";
        private const string Typed = "Chodźmy do miasta.";

        private static string Dir =>
            Environment.GetEnvironmentVariable("KAPTION_COMMUNITY_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(RepoRoot() ?? throw new AssertInconclusiveException("Repository root not found."),
                    "landing", "src", "assets", "community");

        [TestMethod]
        public void Community_FixWindow()
        {
            if (Environment.GetEnvironmentVariable("KAPTION_RENDER_COMMUNITY") != "1")
                Assert.Inconclusive("Set KAPTION_RENDER_COMMUNITY=1 for the community pages' fix-window renders.");
            var written = new List<string>();
            RunSta(() =>
            {
                using var dir = new TempFolder();
                var line = new ShownLine("Genshin", "pl", English, Current, Current, "Paimon", null, DateTime.UtcNow);
                foreach (var (lang, tag) in new[] { ("pl-PL", "pl"), ("en-US", "en") })
                {
                    var w = new SuggestFixWindow(line, dir.Layers(), null, IntPtr.Zero);
                    try
                    {
                        var box = (TextBox)w.FindName("FixBox");
                        box.Text = Typed;
                        Assert.IsTrue(((Button)w.FindName("BtnSave")).IsEnabled, "the typed fix can be saved");
                        Assert.AreEqual(Current, ((TextBlock)w.FindName("CurrentText")).Text);
                        var host = Host(w, lang);
                        Layout(host, Width);
                        written.Add(Write(Snapshot(host, Scale), $"fix-window-{tag}.png"));
                    }
                    finally
                    {
                        w.Close();
                    }
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        /// <summary>The window's content in a host carrying the dark palette, as SuggestFixWindowTests renders it.</summary>
        private static Border Host(Window w, string lang)
        {
            var content = (UIElement)w.Content;
            var resources = w.Resources;
            w.Content = null;
            w.Resources = new ResourceDictionary();
            var host = new Border { Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true };
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Palette.Dark.xaml"));
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Shell.xaml"));
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            host.Resources.MergedDictionaries.Add(resources);
            host.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            host.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            return host;
        }

        private static string Write(BitmapSource bitmap, string name)
        {
            AssertNotBlank(bitmap, name);
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
    }
}
