using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>The setup window: flow rules (pure) and every step rendered in both palettes and languages.</summary>
    [TestClass]
    public class OnboardingTests
    {
        // ── flow ────────────────────────────────────────────────────────────

        [TestMethod]
        public void StepsRunInTheLeadsOrder()
        {
            CollectionAssert.AreEqual(
                new[] { OnboardingStep.Welcome, OnboardingStep.Game, OnboardingStep.SignIn, OnboardingStep.Prepare, OnboardingStep.Done },
                OnboardingFlow.Steps.ToArray(), "the game step comes before the sign-in wall");
        }

        [TestMethod]
        public void Welcome_NeedsTheLicence()
        {
            var f = new OnboardingFlow(OnboardingMode.FirstRun);
            Assert.IsFalse(f.CanAdvance);
            Assert.IsFalse(f.Advance());
            f.EulaAccepted = true;
            Assert.IsTrue(f.Advance());
            Assert.AreEqual(OnboardingStep.Game, f.Step);
        }

        [TestMethod]
        public void Game_NeedsAGame_SignIn_NeedsASession_Prepare_NeedsKeyAndPack()
        {
            var f = new OnboardingFlow(OnboardingMode.FirstRun) { EulaAccepted = true };
            f.Advance();
            Assert.IsFalse(f.CanAdvance, "no game picked yet");
            f.Game = "Genshin";
            Assert.IsTrue(f.Advance());
            Assert.IsFalse(f.CanAdvance, "not signed in");
            f.SignedIn = true;
            Assert.IsTrue(f.Advance());
            Assert.AreEqual(OnboardingStep.Prepare, f.Step);
            f.KeyReady = true;
            Assert.IsFalse(f.CanAdvance, "Gotowe waits for the pack, not the index");
            f.PackSettled = true;
            Assert.IsTrue(f.Advance());
            Assert.AreEqual(OnboardingStep.Done, f.Step);
            Assert.IsFalse(f.Advance(), "nothing after Done");
        }

        [TestMethod]
        public void BackOnlyFromGameAndSignIn()
        {
            var f = new OnboardingFlow(OnboardingMode.FirstRun) { EulaAccepted = true, Game = "ZZZ", SignedIn = true, KeyReady = true, PackSettled = true };
            Assert.IsFalse(f.Back(), "nothing before Welcome");
            f.Advance();
            Assert.IsTrue(f.Back());
            f.Advance(); f.Advance(); f.Advance();
            Assert.AreEqual(OnboardingStep.Prepare, f.Step);
            Assert.IsFalse(f.Back(), "preparing only moves forward");
            Assert.IsFalse(f.GoTo(OnboardingStep.Game), "no jumping back once the key and pack are in flight");
        }

        [TestMethod]
        [DataRow(false, false, false, 2)]
        [DataRow(true, false, false, 1)]
        [DataRow(true, true, false, 3)]
        [DataRow(true, true, true, 0)]
        public void ClosingEarlyMapsToTheOldExitCodes(bool eula, bool signedIn, bool key, int expected)
        {
            var f = new OnboardingFlow(OnboardingMode.FirstRun) { EulaAccepted = eula, SignedIn = signedIn, KeyReady = key };
            Assert.AreEqual(expected, f.ExitCodeIfClosed);
            Assert.AreEqual(0, new OnboardingFlow(OnboardingMode.Rerun).ExitCodeIfClosed, "the setup guide never quits the app");
        }

        [TestMethod]
        [DataRow(false, false, true)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(true, true, false)]
        public void OnlyNewInstallsGetTheFirstRun(bool completed, bool dismissed, bool expected)
        {
            Assert.AreEqual(expected, OnboardingFlow.IsFirstRunNeeded(completed, dismissed),
                "2.3 and 2.4 users completed or dismissed the old wizard and must not see setup again");
        }

        [TestMethod]
        [DataRow("pl-PL", "pl-PL")]
        [DataRow("pl", "pl-PL")]
        [DataRow("en-GB", "en-US")]
        [DataRow("de-DE", "en-US")]
        [DataRow(null, "en-US")]
        public void InterfaceLanguageFollowsWindows(string windows, string expected)
        {
            Assert.AreEqual(expected, UiLanguage.FromWindows(windows));
        }

        [TestMethod]
        [DataRow("Akceptuję {0} i {1}.", "Akceptuję ", " i ", ".")]
        [DataRow("I accept the {0} and the {1}.", "I accept the ", " and the ", ".")]
        [DataRow("broken", "I accept the ", " and the ", ".")]
        [DataRow(null, "I accept the ", " and the ", ".")]
        public void TheLicenceSentenceKeepsItsSpaces(string format, string prefix, string mid, string suffix)
        {
            Assert.AreEqual((prefix, mid, suffix), OnboardingWindow.SplitAroundLinks(format));
        }

        [TestMethod]
        [DataRow("Na co dzień", "N\u200aA\u2002C\u200aO\u2002D\u200aZ\u200aI\u200aE\u200aŃ")]
        [DataRow("Krok 1 z 5", "K\u200aR\u200aO\u200aK\u20021\u2002Z\u20025")]
        [DataRow("  a  b ", "A\u2002B")]
        public void EyebrowsTrackLettersAndWidenWordGaps(string text, string expected)
        {
            Assert.AreEqual(expected, Typo.Track(text, new CultureInfo("pl-PL")));
        }

        // ── renders ─────────────────────────────────────────────────────────

        [TestMethod]
        public void RendersEveryStepInBothPalettesAndLanguages()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                foreach (string palette in WindowRenderTests.Palettes)
                foreach (string lang in new[] { "pl-PL", "en-US" })
                foreach (OnboardingStep step in OnboardingFlow.Steps)
                {
                    var host = LoadLooseWindow("Views/OnboardingWindow.xaml", lang, out var window, palette);
                    Fill(host, window, step, lang);
                    Layout(host, 1000, 720);
                    string name = $"setup-{(int)step + 1}-{step.ToString().ToLowerInvariant()}-{lang.Substring(0, 2)}-{palette.ToLowerInvariant()}.png";
                    written.Add(SaveGuide(Snapshot(host), name));
                    written.Add(Save(Snapshot(host), "window-" + name));
                    written.Add(SaveReview(Snapshot(host), "setup/" + name));
                    if (lang == "pl-PL") written.Add(Save(Snapshot(host, 1.25), "window-" + name.Replace(".png", "@125.png")));
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        private static string SaveGuide(BitmapSource bitmap, string name)
        {
            Directory.CreateDirectory(ShellRenderTests.GuidesDir);
            string path = Path.Combine(ShellRenderTests.GuidesDir, name);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        /// <summary>What the window's code shows on each step, with a Genshin PC's data.</summary>
        internal static void Fill(Border host, Window window, OnboardingStep step, string lang)
        {
            string L(string key, string fallback = "") => host.TryFindResource(key) as string ?? fallback;
            var culture = new CultureInfo(lang);
            object Find(string n) => window.FindName(n);
            string repo = RepoRoot();
            string art = Path.Combine(repo, "GI-Subtitles", "Assets", "Ambience", AmbienceArt.Welcome + ".jpg");
            if (File.Exists(art)) ((ImageBrush)Find("RailArt")).ImageSource = AmbienceArt.LoadFile(art);
            ((TitleBar)Find("WindowTitleBar")).Subtitle = L("onb_Window_Subtitle");

            var rail = new ObservableCollection<OnboardingWindow.RailItem>();
            string[] railKeys = { "onb_Rail_Welcome", "onb_Rail_Game", "onb_Rail_SignIn", "onb_Rail_Prepare", "onb_Rail_Done" };
            for (int i = 0; i < railKeys.Length; i++)
                rail.Add(new OnboardingWindow.RailItem { Label = L(railKeys[i]), State = i < (int)step ? "Done" : i == (int)step ? "Current" : "Upcoming" });
            ((ItemsControl)Find("StepList")).ItemsSource = rail;

            string key = new[] { "Welcome", "Game", "SignIn", "Prepare", "Done" }[(int)step];
            Typo.SetEyebrow((TextBlock)Find("StepEyebrow"), string.Format(culture, L("onb_Step_Format", "{0}/{1}"), (int)step + 1, 5));
            ((TextBlock)Find("StepHeading")).Text = L($"onb_{key}_Heading");
            ((TextBlock)Find("StepLead")).Text = L($"onb_{key}_Lead");
            ((TextBlock)Find("StepLead")).Visibility = step == OnboardingStep.Done ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (panel, s) in new[] { ("WelcomePanel", OnboardingStep.Welcome), ("GamePanel", OnboardingStep.Game), ("SignInPanel", OnboardingStep.SignIn),
                                                ("PreparePanel", OnboardingStep.Prepare), ("DonePanel", OnboardingStep.Done) })
                ((UIElement)Find(panel)).Visibility = s == step ? Visibility.Visible : Visibility.Collapsed;

            // Footer, as UpdateFooter sets it.
            ((UIElement)Find("QuitButton")).Visibility = step == OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
            ((UIElement)Find("BackButton")).Visibility = step is OnboardingStep.Game or OnboardingStep.SignIn ? Visibility.Visible : Visibility.Collapsed;
            ((UIElement)Find("NextButton")).Visibility = step == OnboardingStep.Done ? Visibility.Collapsed : Visibility.Visible;
            ((UIElement)Find("NextButton")).IsEnabled = step != OnboardingStep.Prepare && step != OnboardingStep.SignIn;
            ((UIElement)Find("FinishButton")).Visibility = step == OnboardingStep.Done ? Visibility.Visible : Visibility.Collapsed;
            ((UIElement)Find("SecondaryButton")).Visibility = step == OnboardingStep.Done ? Visibility.Visible : Visibility.Collapsed;

            switch (step)
            {
                case OnboardingStep.Welcome:
                    ((RadioButton)Find(lang == "pl-PL" ? "LangPl" : "LangEn")).IsChecked = true;
                    var (prefix, mid, suffix) = OnboardingWindow.SplitAroundLinks(L("onb_Welcome_Eula_Format"));
                    ((System.Windows.Documents.Run)Find("RunEulaPrefix")).Text = prefix;
                    ((System.Windows.Documents.Run)Find("RunEulaLink1")).Text = L("onb_Welcome_Eula_Link1");
                    ((System.Windows.Documents.Run)Find("RunEulaMid")).Text = mid;
                    ((System.Windows.Documents.Run)Find("RunEulaLink2")).Text = L("onb_Welcome_Eula_Link2");
                    ((System.Windows.Documents.Run)Find("RunEulaSuffix")).Text = suffix;
                    ((CheckBox)Find("EulaCheck")).IsChecked = true;
                    break;
                case OnboardingStep.Game:
                    ((StatusMark)Find("DetectMark")).State = MarkState.Done;
                    ((TextBlock)Find("DetectText")).Text = string.Format(culture, L("onb_Game_Detected_Format", "{0}"), "Genshin Impact");
                    ((RadioButton)Find("GameGenshin")).IsChecked = true;
                    ((TextBlock)Find("RegionStatus")).Text = L("onb_Region_Auto");
                    ((TextBlock)Find("RegionMonitor")).Text = string.Format(culture, L("shell_Region_Monitor_Format", "{0}"), 1, 2560, 1440);
                    ((UIElement)Find("DisplayModeNotice")).Visibility = Visibility.Visible;
                    string frame = new[]
                    {
                        Path.Combine(repo, "GI-Test", "bin", "Debug", "Images", "en-pl-npc-dialog.jpg"),
                        @"F:\Pobrane\Nowy folder (3)\kaption-promo-landing\GI-Test\bin\Debug\Images\en-pl-npc-dialog.jpg",
                    }.FirstOrDefault(File.Exists);
                    if (frame != null)
                    {
                        var shot = new BitmapImage(new Uri(frame));
                        var region = new System.Drawing.Rectangle(160, 955, 1650, 255);
                        var area = SettingsWindow.PreviewArea(region, new System.Drawing.Rectangle(0, 0, shot.PixelWidth, shot.PixelHeight));
                        ((Image)Find("RegionPreviewImage")).Source = SettingsWindow.ComposeRegionPreview(
                            new CroppedBitmap(shot, new Int32Rect(area.X, area.Y, area.Width, area.Height)),
                            new Rect(region.X - area.X, region.Y - area.Y, region.Width, region.Height), host.TryFindResource("GameGenshinBrush") as Brush);
                        ((UIElement)Find("RegionPreviewEmpty")).Visibility = Visibility.Collapsed;
                    }
                    break;
                case OnboardingStep.Prepare:
                    ((StatusMark)Find("PackMark")).State = MarkState.Working;
                    ((TextBlock)Find("PackText")).Text = string.Format(culture, L("onb_Prepare_Pack_Working_Format", "{0} {1} {2}"),
                        "Genshin Impact", 42, PackStatus.FormatSize(100_663_296, culture));
                    var bar = (ProgressBar)Find("PackBar");
                    bar.Visibility = Visibility.Visible;
                    bar.Value = 0.42;
                    ((TextBlock)Find("ReaderText")).Text = string.Format(culture, L("onb_Prepare_Reader_Format", "{0}"), "GPU");
                    break;
                case OnboardingStep.Done:
                    ((TextBlock)Find("DoneGameText")).Text = string.Format(culture, L("onb_Done_Game_Format", "{0}"), "Genshin Impact");
                    ((TextBlock)Find("DoneRegionText")).Text = L("onb_Done_Region_Set");
                    ((TextBlock)Find("DonePackText")).Text = L("onb_Done_Pack");
                    ((TextBlock)Find("DoneStartText")).Text = L("onb_Done_AutoStart");
                    ((TextBlock)Find("DoneIndexText")).Text = L("onb_Done_Index");
                    ((ItemsControl)Find("DoneHotkeys")).ItemsSource = new[]
                    {
                        new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9000"), Keys = new[] { "Ctrl", "Shift", "S" } },
                        new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9005"), Keys = new[] { "Ctrl", "Q" } },
                        new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9004"), Keys = new[] { "Ctrl", "1" } },
                        new SettingsWindow.HotkeyCardRow { Label = L("shell_Hotkey_Short_9002"), Keys = new[] { "Ctrl", "Shift", "H" } },
                    };
                    break;
            }
        }
    }
}
