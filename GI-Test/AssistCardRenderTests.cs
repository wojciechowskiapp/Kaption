using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Settings, "Pomocnik w grze": the card on the Game page with a reader's switch and sentence, what was read and
    /// the delete button, in both languages and palettes, and the empty state of a game with nothing measured.
    /// Pictures go to %TEMP%\kaption-render\assist\.
    /// </summary>
    [TestClass]
    public class AssistCardRenderTests
    {
        [TestMethod]
        public void TheCard_ShowsTheReaderItsSwitchAndWhatWasRead()
        {
            var written = new List<string>();
            var at = new DateTimeOffset(2026, 11, 10, 12, 5, 0, TimeSpan.Zero);
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                {
                    var culture = CultureInfo.GetCultureInfo(lang);
                    var host = LoadLooseWindow("Views/SettingsWindow.xaml", lang, out var window, palette);
                    ShellRenderTests.Fill(host, window, ShellRenderTests.Scene.Stopped, lang);
                    var tabs = (TabControl)window.FindName("NavTabs");
                    foreach (TabItem t in tabs.Items) if ((string)t.Tag == SettingsNavigation.Game) tabs.SelectedItem = t;
                    Func<string, string, string> l = (key, fallback) => host.TryFindResource(key) as string ?? fallback;

                    // A game with a reader, switched on, with a reading.
                    GameManifest genshin = GameRegistry.Current.FindByWireId("genshin");
                    AssistCardModel model = AssistCardModel.Build(genshin, _ => true,
                        new[] { new AssistStoredReading(AssistStore.LocalAccount, "stamina", "121/200", "", at) }, l, culture);
                    SettingsWindow.ApplyAssistCardTo(window.FindName, model, l, null);
                    Layout(host, 1280, 1500);

                    var card = (FrameworkElement)window.FindName("AssistCard");
                    card.BringIntoView();
                    Layout(host, 1280, 1500);
                    Assert.IsTrue(card.ActualHeight > 0 && card.ActualWidth > 0, "on the page, not behind Zaawansowane");
                    var readers = (Panel)window.FindName("AssistReaderList");
                    var toggle = readers.Children.OfType<CheckBox>().Single();
                    Assert.AreEqual(true, toggle.IsChecked);
                    Assert.IsTrue(toggle.ActualHeight > 0);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(toggle)), "a screen reader hears the switch's name");
                    Assert.IsTrue(((Button)window.FindName("AssistDeleteButton")).IsEnabled);
                    var line = ((Panel)window.FindName("AssistReadLines")).Children.OfType<TextBlock>().Single();
                    StringAssert.Contains(line.Text, "Original Resin: 121/200");
                    Assert.IsTrue(line.ActualWidth <= card.ActualWidth, "the line stays inside the card");
                    written.Add(SaveReview(Snapshot(host), $"assist/card-on-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));

                    // A new reading arrives while the keyboard is on the switch: the switch stays the control it is.
                    SettingsWindow.ApplyAssistCardTo(window.FindName, AssistCardModel.Build(genshin, _ => true,
                        new[] { new AssistStoredReading(AssistStore.LocalAccount, "stamina", "122/200", "", at.AddMinutes(8)) }, l, culture), l, null);
                    Assert.AreSame(toggle, readers.Children.OfType<CheckBox>().Single(), "a refresh does not take the focused switch away");

                    // Off, nothing read yet: the delete button has nothing to delete.
                    SettingsWindow.ApplyAssistCardTo(window.FindName, AssistCardModel.Build(genshin, _ => false, null, l, culture), l, null);
                    Layout(host, 1280, 1500);
                    Assert.AreEqual(false, readers.Children.OfType<CheckBox>().Single().IsChecked, "off until the player switches it on");
                    Assert.IsFalse(((Button)window.FindName("AssistDeleteButton")).IsEnabled);
                    written.Add(SaveReview(Snapshot(host), $"assist/card-off-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));

                    // A game with nothing measured says so.
                    SettingsWindow.ApplyAssistCardTo(window.FindName, AssistCardModel.Build(GameRegistry.Current.FindByWireId("zzz"), _ => true, null, l, culture), l, null);
                    Layout(host, 1280, 1500);
                    var none = (TextBlock)window.FindName("AssistNone");
                    Assert.IsTrue(none.ActualHeight > 0);
                    StringAssert.Contains(none.Text, "Zenless Zone Zero");
                    Assert.AreEqual(Visibility.Collapsed, ((UIElement)window.FindName("AssistReadBlock")).Visibility);
                    written.Add(SaveReview(Snapshot(host), $"assist/card-none-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png"));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }
    }
}
