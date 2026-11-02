using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
// GI_Test has a Config of its own; these windows read and write the app's.
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// Windows built with their code-behind, as the app builds them. The render tests load the XAML loose, without
    /// handlers, so they cannot see a handler that runs while InitializeComponent applies the XAML defaults: in 2.5
    /// RC that turned a saved "off" for auto-start back on at every launch and threw before Settings could open.
    /// </summary>
    [TestClass]
    public class WindowConstructionTests
    {
        private static T Find<T>(System.Windows.FrameworkElement window, string name) where T : class =>
            window.FindName(name) as T ?? throw new AssertFailedException($"no {name} in {window.GetType().Name}");

        private static string ConfigFile => Path.Combine(TestEnvironment.ConfigDir, "Config.json");

        /// <summary>The saved settings, key by key, as the app wrote them to disk.</summary>
        private static Dictionary<string, string> Saved() =>
            (JsonNode.Parse(File.ReadAllText(ConfigFile)) as JsonObject)?
                .ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null") ?? new Dictionary<string, string>();

        private static void AssertUnchanged(Dictionary<string, string> before, string what)
        {
            var after = Saved();
            var changed = before.Keys.Union(after.Keys)
                .Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
                .Select(k => $"{k}: {(before.TryGetValue(k, out var b) ? b : "(none)")} -> {(after.TryGetValue(k, out var a) ? a : "(none)")}")
                .ToList();
            Assert.AreEqual(0, changed.Count, $"{what} changed saved settings: {string.Join("; ", changed)}");
        }

        [TestMethod]
        public void TheAppsSettings_GoToATemporaryProfile()
        {
            AppConfig.Set("Tests.Probe", 1);
            StringAssert.StartsWith(Path.GetFullPath(ConfigFile), Path.GetFullPath(Path.GetTempPath()));
            StringAssert.Contains(File.ReadAllText(ConfigFile), "Tests.Probe", "tests never read or write a real profile");
        }

        [TestMethod]
        public void Settings_KeepsASavedOff_ForBothAutoStartSettings_AndTheUsersChangesStillWrite()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                AppConfig.Set(SettingsWindow.AutoStartFocusKey, false);
                AppConfig.Set(SettingsWindow.Intro25SeenKey, false); // a returning user who has not dismissed the 2.5 card

                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true), "building the window keeps a saved off");
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartFocusKey, true), "the same for the focus setting");
                    var toggle = Find<CheckBox>(w, "AutoTranslateToggle");
                    var card = Find<CheckBox>(w, "Intro25AutoToggle");
                    var focus = Find<CheckBox>(w, "AutoTranslateFocusCheck");
                    Assert.AreEqual(false, toggle.IsChecked, "the switch shows the saved setting, not the XAML default");
                    Assert.AreEqual(false, card.IsChecked);
                    Assert.AreEqual(false, focus.IsChecked);

                    // The user's own changes still write, from either switch, and both show them.
                    toggle.IsChecked = true;
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, false));
                    Assert.AreEqual(true, card.IsChecked);
                    card.IsChecked = false;
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true));
                    Assert.AreEqual(false, toggle.IsChecked);
                    focus.IsChecked = true;
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartFocusKey, false));

                    // "Rozumiem" on the 2.5 card writes only its own key.
                    var before = Saved();
                    typeof(SettingsWindow).GetMethod("Intro25Ok_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(w, new object[] { null, null });
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.Intro25SeenKey, false));
                    before[SettingsWindow.Intro25SeenKey] = "true";
                    AssertUnchanged(before, "Rozumiem");
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Settings_DefaultsToOn_WhenNothingWasSaved_WithoutWritingIt()
        {
            RunSta(() =>
            {
                AppConfig.Remove(SettingsWindow.AutoStartTranslationKey);
                AppConfig.Remove(SettingsWindow.AutoStartFocusKey);
                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.AreEqual(true, Find<CheckBox>(w, "AutoTranslateToggle").IsChecked);
                    Assert.AreEqual(true, Find<CheckBox>(w, "Intro25AutoToggle").IsChecked);
                    Assert.AreEqual(true, Find<CheckBox>(w, "AutoTranslateFocusCheck").IsChecked);
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.AutoStartTranslationKey), "showing the default is not a choice");
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.AutoStartFocusKey));
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Settings_BuildingTheWindow_ChangesNoSavedSetting()
        {
            RunSta(() =>
            {
                // Each unlike its control's XAML default, so a write while the XAML loads shows up here.
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                AppConfig.Set(SettingsWindow.AutoStartFocusKey, false);
                AppConfig.Set("AutoShrinkText", false);
                AppConfig.Set("FontFamily", "Consolas");
                AppConfig.Set("Size", 30);
                var before = Saved();
                // Settings builds its file protection while it is constructed; see TestProtection.UseFakeLicense.
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    AssertUnchanged(before, "building Settings");
                    Assert.AreEqual(false, Find<CheckBox>(w, "AutoShrinkCheckBox").IsChecked);
                    Assert.AreEqual("Consolas", (Find<ComboBox>(w, "FontFamilyComboBox").SelectedItem as ComboBoxItem)?.Tag);
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Setup_ShowsASavedOff_AndItsSkipBoxWritesTheSameSetting()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                var before = Saved();
                var w = new OnboardingWindow(null, OnboardingMode.Rerun);
                try
                {
                    AssertUnchanged(before, "building the setup");
                    var skip = Find<CheckBox>(w, "SkipAutoStartCheck");
                    Assert.AreEqual(true, skip.IsChecked, "the setup shows the saved off as \"don't start by itself\"");

                    // Leaving the Gra step commits the box: unticked turns auto-start on, ticked turns it off.
                    var leave = typeof(OnboardingWindow).GetMethod("LeaveStep", BindingFlags.Instance | BindingFlags.NonPublic);
                    skip.IsChecked = false;
                    leave.Invoke(w, new object[] { OnboardingStep.Game });
                    Assert.IsTrue(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, false));
                    skip.IsChecked = true;
                    leave.Invoke(w, new object[] { OnboardingStep.Game });
                    Assert.IsFalse(AppConfig.Get(SettingsWindow.AutoStartTranslationKey, true));
                }
                finally
                {
                    w.Close();
                }
            });
        }

        [TestMethod]
        public void Looks_KinoMovesTheSubtitle_AndAnotherLookPutsItBack()
        {
            RunSta(() =>
            {
                foreach (string k in new[] { "Size", "SubtitleBgOpacity", "Overlay.Outline", "Overlay.LetterSpacing", "Overlay.Font", "FontFamily",
                             SettingsWindow.PadBeforeCinemaKey })
                    AppConfig.Remove(k);
                AppConfig.Set("Pad", new[] { -187, -318 });
                using var license = TestProtection.UseFakeLicense();
                var w = new SettingsWindow("2.5.0", null, 1.0);
                try
                {
                    Assert.AreEqual(false, Find<RadioButton>(w, "PresetCinema").IsChecked, "building the page picks nothing");
                    Find<RadioButton>(w, "PresetCinema").IsChecked = true;
                    var kino = GI_Subtitles.Services.Rendering.OverlayStyle.FromConfig();
                    Assert.AreEqual(GI_Subtitles.Services.Rendering.OverlayPreset.Cinema, kino.MatchingPreset());
                    Assert.AreEqual(0, kino.PadHorizontal, "Kino centres the subtitle");
                    Assert.AreNotEqual(-187, kino.PadVertical, "and moves it down to the dialogue");
                    Assert.AreEqual(26, (int)Find<Slider>(w, "FontSizeSlider").Value, "the size slider follows the look");

                    Find<RadioButton>(w, "PresetClassic").IsChecked = true;
                    var back = GI_Subtitles.Services.Rendering.OverlayStyle.FromConfig();
                    Assert.AreEqual(GI_Subtitles.Services.Rendering.OverlayPreset.Classic, back.MatchingPreset());
                    Assert.AreEqual((-187, -318), (back.PadVertical, back.PadHorizontal), "the position from before Kino comes back");
                    Assert.IsFalse(AppConfig.Has(SettingsWindow.PadBeforeCinemaKey));
                }
                finally
                {
                    w.Close();
                    AppConfig.Remove("Pad");
                    foreach (string k in new[] { "Size", "SubtitleBgOpacity", "Overlay.Outline", "Overlay.LetterSpacing" }) AppConfig.Remove(k);
                }
            });
        }

        [TestMethod]
        public void Overlay_BuildingTheWindow_ChangesNoSavedSetting()
        {
            RunSta(() =>
            {
                AppConfig.Set(SettingsWindow.AutoStartTranslationKey, false);
                var before = Saved();
                var w = new MainWindow();
                try
                {
                    AssertUnchanged(before, "building the overlay");
                }
                finally
                {
                    w.Close();
                }
            });
        }
    }
}
