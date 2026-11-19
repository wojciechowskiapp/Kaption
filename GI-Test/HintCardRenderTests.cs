using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The in-game hint card drawn through its own window's content, never shown on screen: the sheet, the short
    /// sheet, an echo, a tip and the strip, in every listed game's accent, in Polish, and in English for the first
    /// game. Output: %TEMP%\kaption-render\hint-card-*.png, for the reviewer and the owner to look at. It also
    /// holds the two things a picture cannot be trusted to show: no label is cut off, and every sentence fits in
    /// two lines.
    /// </summary>
    [TestClass]
    public class HintCardRenderTests
    {
        private static readonly Color Backdrop = Color.FromRgb(0x3A, 0x45, 0x55);

        private static HintBindings Defaults => HintBindings.From(id => id switch
        {
            HintCatalog.KeyToggleTranslation => "Ctrl+Shift+S",
            HintCatalog.KeyHideSubtitles => "Ctrl+Shift+H",
            HintCatalog.KeyShowDialogueFrame => "Ctrl+Shift+D",
            HintCatalog.KeyQuickTranslate => "Ctrl+Q",
            HintCatalog.KeyFixLine => "Ctrl+Shift+E",
            _ => null,
        });

        /// <summary>The app's own lookup: the chosen language over English, else the fallback.</summary>
        private static Func<string, string, string> TextIn(string lang)
        {
            var dictionaries = Strings(lang).Reverse().ToList();
            return (key, fallback) => dictionaries.Select(d => d[key] as string).FirstOrDefault(s => s != null) ?? fallback;
        }

        /// <summary>A window's content on a backdrop, with what the app gives it at run time: tokens and strings.</summary>
        private static Border Lift(Window window, string lang)
        {
            var chrome = (FrameworkElement)window.Content;
            ResourceDictionary resources = window.Resources;
            window.Content = null;
            window.Resources = new ResourceDictionary();
            chrome.HorizontalAlignment = HorizontalAlignment.Left; // the window sizes itself to its content
            var host = new Border { Child = chrome, UseLayoutRounding = true, Margin = new Thickness(0, 0, 0, 14) };
            host.Resources.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
            host.Resources.MergedDictionaries.Add(resources);
            foreach (var d in Strings(lang)) host.Resources.MergedDictionaries.Add(d);
            return host;
        }

        private static (Border Host, HintCardWindow Window) Card(HintCardViewModel model, string game, string lang, bool compact = false)
        {
            var window = new HintCardWindow();
            window.SetContent(model, GameAccents.For(game), compact);
            return (Lift(window, lang), window);
        }

        private static Border Strip(HintStripMode mode, bool pinned, string lang)
        {
            var window = new HintStripWindow();
            window.SetMode(mode, pinned);
            return Lift(window, lang);
        }

        [TestMethod]
        public void RendersTheCardInEveryGamesAccent()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                IReadOnlyList<GameManifest> games = GameRegistry.Current.Listed;
                Assert.IsTrue(games.Count > 0, "the registry lists games");
                var cases = games.Select(g => (Game: g, Lang: "pl-PL")).Append((games[0], "en-US"));
                foreach (var (game, lang) in cases)
                {
                    Func<string, string, string> text = TextIn(lang);
                    string name = $"{game.Id}-{lang[..2]}";
                    var stack = new StackPanel();

                    var (sheetHost, sheet) = Card(HintCardViewModel.Sheet(Defaults, text, clock: true), game.GameId, lang);
                    stack.Children.Add(sheetHost);
                    stack.Children.Add(Card(HintCardViewModel.Sheet(Defaults, text, clock: false), game.GameId, lang, compact: true).Host);

                    var lines = new List<(HintEntry Entry, HintCardWindow Window)>();
                    foreach (HintEntry entry in HintCatalog.Entries.Where(e => e.Kind is HintEntryKind.Echo or HintEntryKind.StateTip or HintEntryKind.TeachingTip))
                    {
                        var (host, window) = Card(HintCardViewModel.Line(entry, Defaults, text), game.GameId, lang);
                        stack.Children.Add(host);
                        lines.Add((entry, window));
                    }
                    stack.Children.Add(Strip(HintStripMode.Sheet, pinned: false, lang));
                    stack.Children.Add(Strip(HintStripMode.Sheet, pinned: true, lang));
                    stack.Children.Add(Strip(HintStripMode.Never, pinned: false, lang));

                    var page = new Border { Child = stack, Padding = new Thickness(24, 24, 24, 10), Background = new SolidColorBrush(Backdrop), UseLayoutRounding = true };
                    TextOptions.SetTextFormattingMode(page, TextFormattingMode.Display);
                    Layout(page, 480);

                    // No label is cut off: the sheet is as wide as its longest row needs, in both languages.
                    var rows = (ItemsControl)sheet.FindName("RowList");
                    var labels = Descendants<TextBlock>(rows).Where(t => t.TextTrimming == TextTrimming.CharacterEllipsis).ToList();
                    Assert.AreEqual(HintCatalog.SheetRows.Count, labels.Count, name);
                    foreach (TextBlock label in labels)
                    {
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        Assert.IsTrue(label.DesiredSize.Width <= label.ActualWidth + 0.5, $"{name}: \"{label.Text}\" is cut off ({label.DesiredSize.Width:0} in {label.ActualWidth:0})");
                    }
                    // Every sentence is one or two lines.
                    foreach (var (entry, window) in lines)
                    {
                        var message = (TextBlock)window.FindName("MessageText");
                        Assert.IsTrue(message.ActualHeight <= message.LineHeight * 2 + 1, $"{name}: {entry.Id} takes {message.ActualHeight / message.LineHeight:0.#} lines");
                    }

                    written.Add(Save(Snapshot(page), $"hint-card-{name}.png"));
                    if (ReferenceEquals(game, games[0])) written.Add(Save(Snapshot(page, 2), $"hint-card-{name}@2x.png"));
                }
            });
            foreach (string path in written) Console.WriteLine(path);
        }

        /// <summary>
        /// The shortcuts sheet over a real Honkai: Star Rail dialogue, as a player first meets it in the game, for the site's
        /// setup guide (KAPTION_RENDER_GUIDES=1; landing/scripts/guide-shots.mjs makes the WebP). The card is drawn through its
        /// own window's content like the renders above, over the scene's picture; nothing is shown on screen.
        /// </summary>
        [TestMethod]
        public void RendersTheSheetOverAScene_ForTheGuides()
        {
            if (!GuideShotTests.PublishRequested) Assert.Inconclusive("Set KAPTION_RENDER_GUIDES=1 for the guides' publishing renders.");
            string scenePath = RenderFixtures.Scene("honkai.jpg"); // a real Star Rail dialogue with the Polish line above it
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var (cardHost, _) = Card(HintCardViewModel.Sheet(Defaults, TextIn(lang), clock: true), "StarRail", lang);
                    cardHost.Margin = new Thickness(0);
                    cardHost.LayoutTransform = new ScaleTransform(1.25, 1.25);
                    cardHost.HorizontalAlignment = HorizontalAlignment.Right;
                    cardHost.VerticalAlignment = VerticalAlignment.Top;
                    var scene = new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(scenePath)), Stretch = Stretch.Fill,
                    };
                    var stage = new Grid { Width = 1280, Height = 685 };
                    stage.Children.Add(scene);
                    cardHost.Margin = new Thickness(0, 28, 28, 0);
                    stage.Children.Add(cardHost);
                    var page = new Border { Child = stage, UseLayoutRounding = true };
                    Layout(page, 1280, 685);
                    var bitmap = Snapshot(page);
                    System.IO.Directory.CreateDirectory(GuideShotTests.PublishDir);
                    string path = System.IO.Path.Combine(GuideShotTests.PublishDir, $"app-hint-card-area-{lang[..2]}.png");
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var fs = System.IO.File.Create(path)) encoder.Save(fs);
                    written.Add(path);
                }
            });
            foreach (string path in written) Console.WriteLine(path);
        }

        /// <summary>
        /// The size the window is placed by is the size of the card being placed, not of the one that was on it:
        /// a sheet that was just pinned has no clock bar, an echo has none either.
        /// </summary>
        [TestMethod]
        public void TheSizeMeasuredForACard_IsItsOwn_WhateverWasOnTheWindowBefore()
        {
            RunSta(() =>
            {
                Func<string, string, string> text = TextIn("pl-PL");
                HintCardViewModel timed = HintCardViewModel.Sheet(Defaults, text, clock: true), pinned = HintCardViewModel.Sheet(Defaults, text, clock: false);
                Color accent = GameAccents.For("Genshin");

                var fresh = new HintCardWindow();
                fresh.SetContent(pinned, accent);
                Size full = fresh.MeasureContent(pinned, compact: false), compact = fresh.MeasureContent(pinned, compact: true);

                var reused = new HintCardWindow();
                reused.SetContent(timed, accent);
                Size timedFull = reused.MeasureContent(timed, compact: false);
                Assert.AreEqual(full, reused.MeasureContent(pinned, compact: false), "the pinned sheet's own size");
                Assert.AreEqual(compact, reused.MeasureContent(pinned, compact: true), "and its short form's");
                Assert.IsTrue(timedFull.Height > full.Height, $"the clock bar has a height ({timedFull.Height} against {full.Height})");
                Assert.IsTrue(compact.Height < full.Height, "the short sheet is shorter");
            });
        }

        private const int GwlExStyle = -20;
        private const long WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// The promise the card stands on, read back from Windows: the card passes every click on, the strip takes
        /// its own, and showing either never makes it the window in front, also the second time it is shown. Shows
        /// both windows in the top left corner for well under a second.
        /// </summary>
        [TestMethod]
        public void TheCardPassesClicksOn_AndNeitherWindowEverComesToTheFront()
        {
            RunSta(() =>
            {
                var card = new HintCardWindow();
                var strip = new HintStripWindow();
                try
                {
                    card.SetContent(HintCardViewModel.Sheet(Defaults, TextIn("en-US"), clock: true), GameAccents.For("Genshin"));
                    strip.SetMode(HintStripMode.Sheet, pinned: false);
                    for (int round = 0; round < 2; round++)
                    {
                        card.Present(new System.Drawing.Point(40, 60), fade: false);
                        strip.Present(new System.Drawing.Point(40, 20));
                        DoEvents();
                        IntPtr cardHwnd = new System.Windows.Interop.WindowInteropHelper(card).Handle;
                        IntPtr stripHwnd = new System.Windows.Interop.WindowInteropHelper(strip).Handle;
                        long cardStyle = GetWindowLongPtr(cardHwnd, GwlExStyle).ToInt64(), stripStyle = GetWindowLongPtr(stripHwnd, GwlExStyle).ToInt64();
                        const long never = WsExNoActivate | WsExToolWindow;
                        // Layered and transparent together are what passes a click to another program's window.
                        const long passesClicks = WsExLayered | WsExTransparent;
                        Assert.AreEqual(never | passesClicks, cardStyle & (never | passesClicks), $"round {round}: the card is click-through and never active (0x{cardStyle:X})");
                        Assert.AreEqual(never, stripStyle & (never | WsExTransparent), $"round {round}: the strip takes clicks and is never active (0x{stripStyle:X})");
                        IntPtr front = GetForegroundWindow();
                        Assert.IsTrue(front != cardHwnd && front != stripHwnd, $"round {round}: a hint window came to the front");
                        Assert.IsTrue(card.PhysicalBounds.Width > 100 && card.PhysicalBounds.Location == new System.Drawing.Point(40, 60), $"round {round}: {card.PhysicalBounds}");
                        Assert.AreNotEqual(GI_Subtitles.Services.Capture.WindowsHideAnswer.NotAsked, card.HideAnswer, "the card asked Windows to stay out of capture");
                        card.Dismiss(fade: false);
                        strip.Dismiss();
                        DoEvents();
                        Assert.IsFalse(card.IsUp || strip.IsVisible, $"round {round}: both are gone");
                    }
                }
                finally
                {
                    strip.Close();
                    card.Close();
                }
            });
        }
    }
}
