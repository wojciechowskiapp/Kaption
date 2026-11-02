using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Power users: every setting 2.4 had on screen still has its control in 2.5, on the page the move list names,
    /// either on the page itself or under that page's Zaawansowane.
    /// </summary>
    [TestClass]
    public class AdvancedSettingsReachTests
    {
        // Config key (or what it controlled) → control → page → under Zaawansowane?
        private static readonly (string Setting, string Control, string Page, bool Advanced)[] Map =
        {
            ("Game", "GamePillGenshin", SettingsNavigation.Game, false),
            ("Input", "InputPillEN", SettingsNavigation.Game, false),
            ("Output (pack)", "TranslationsList", SettingsNavigation.Game, false),
            ("AutoStartTranslation", "AutoTranslateToggle", SettingsNavigation.Game, false),
            ("AutoStartRequireFocus", "AutoTranslateFocusCheck", SettingsNavigation.Game, true),
            ("AutoStart (with Windows)", "AutoStartCheckBox", SettingsNavigation.Game, true),
            ("Region (draw)", "RegionCard", SettingsNavigation.Game, false),
            ("Region (numbers)", "RegionX", SettingsNavigation.Game, true),
            ("PlayerName", "PlayerNameTextBox", SettingsNavigation.Game, true),
            ("OcrInterval", "OcrIntervalTextBox", SettingsNavigation.Game, true),
            ("StabilityWindow", "StabilityWindowTextBox", SettingsNavigation.Game, true),
            ("Size", "FontSizeSlider", SettingsNavigation.Look, false),
            ("Size (exact)", "FontSizeTextBox", SettingsNavigation.Look, true),
            // 2.6: the limits default to Auto and live under Zaawansowane (design brief, Wygląd napisów).
            ("MaxWidth", "MaxWidthSlider", SettingsNavigation.Look, true),
            ("MaxHeight", "MaxHeightSlider", SettingsNavigation.Look, true),
            ("Pad", "PadVerticalSlider", SettingsNavigation.Look, false),
            ("Pad (drag)", "LookPreview", SettingsNavigation.Look, false),
            ("Pad (exact)", "PadTextBox", SettingsNavigation.Look, true),
            ("Overlay look (Size, SubtitleBgOpacity, Overlay.Outline, Overlay.LetterSpacing)", "PresetClassic", SettingsNavigation.Look, false),
            ("Overlay.AutoDarken", "AutoDarkenToggle", SettingsNavigation.Look, true),
            // AutoShrinkText: hidden and always on since 2.4, not a control anyone could reach.
            ("Appearance.Theme", "ThemeAutoRadio", SettingsNavigation.Look, false),
            ("UILang", "UILangCombo", SettingsNavigation.Look, false),
            ("FontFamily", "FontFamilyComboBox", SettingsNavigation.Look, true),
            ("OcrModel", "OcrModelComboBox", SettingsNavigation.Help, true),
            ("UseGpuOcr", "UseGpuOcrCheckBox", SettingsNavigation.Help, true),
            ("LogLevel (detailed)", "DetailedLoggingCheckBox", SettingsNavigation.Help, true),
            ("CrashReportingEnabled", "CrashReportingCheckBox", SettingsNavigation.Help, false),
            ("Ui.AdvancedMode", "AdvancedModeToggle", SettingsNavigation.Help, false),
        };

        [TestMethod]
        public void EveryOldSetting_HasItsControlOnTheNamedPage()
        {
            var problems = new List<string>();
            RunSta(() =>
            {
                LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window);
                foreach (var (setting, control, page, advanced) in Map)
                {
                    if (!(window.FindName(control) is DependencyObject el))
                    {
                        problems.Add($"{setting}: no control {control}");
                        continue;
                    }
                    string foundPage = null;
                    bool inExpander = false;
                    for (DependencyObject d = el; d != null; d = LogicalTreeHelper.GetParent(d) ?? VisualTreeHelper.GetParent(d))
                    {
                        if (d is Expander) inExpander = true;
                        if (d is TabItem tab) { foundPage = tab.Tag as string; break; }
                    }
                    if (foundPage != page) problems.Add($"{setting}: {control} is on {foundPage ?? "no page"}, expected {page}");
                    if (inExpander != advanced)
                        problems.Add($"{setting}: {control} {(inExpander ? "is" : "is not")} under Zaawansowane, expected {(advanced ? "under" : "on the page")}");
                }
            });
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }
    }
}
