using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>Hotkey 9007 (fix the last subtitle): a free default, pickable in Settings, merged into older files.</summary>
    [TestClass]
    public class FixLineHotkeyTests
    {
        private static HotkeyData Key(int id, bool ctrl, bool shift, char key) =>
            new HotkeyData { Id = id, IsCtrl = ctrl, IsShift = shift, SelectedKey = key, Description = "d" + id };

        [TestMethod]
        public void Default9007_IsCtrlShiftE_FreeAndAvailable()
        {
            var defaults = HotkeySettingsManager.GetDefaultSettings().Hotkeys;
            var fix = defaults.Single(h => h.Id == 9007);
            Assert.AreEqual(HotkeySettingsManager.FixLineHotkeyId, fix.Id);
            Assert.IsTrue(fix.IsCtrl && fix.IsShift && !fix.IsAlt);
            Assert.AreEqual('E', fix.SelectedKey);
            Assert.AreEqual("Ctrl+Shift+E", HotkeySettingsManager.Describe(fix));

            var combos = defaults.Select(HotkeySettingsManager.Describe).ToList();
            Assert.AreEqual(combos.Count, combos.Distinct().Count(), "no two defaults share a combination: " + string.Join(", ", combos));
            var available = HotkeySettingsManager.AvailableKeys();
            foreach (var d in defaults)
                Assert.IsTrue(available.Contains(d.SelectedKey), $"{d.Id}: '{d.SelectedKey}' must be pickable in Settings");
            Assert.AreEqual(defaults.Count, defaults.Select(d => d.Id).Distinct().Count());
        }

        [TestMethod]
        public void Merge_AddsTheFixHotkey_ToOlderSettingsFiles()
        {
            var defaults = HotkeySettingsManager.GetDefaultSettings();
            var beforeQuickTranslateRepeat = new HotkeySettings
            {
                Hotkeys = new List<HotkeyData>
                {
                    Key(9000, true, true, 'S'), Key(9001, true, true, 'R'), Key(9002, true, true, 'H'),
                    Key(9003, true, true, 'D'), Key(9004, true, false, '1'), Key(9005, true, false, 'Q'),
                },
            };
            var merged = HotkeySettingsManager.MergeMissingDefaults(beforeQuickTranslateRepeat, defaults);
            Assert.IsTrue(merged.Hotkeys.Any(h => h.Id == 9006) && merged.Hotkeys.Any(h => h.Id == 9007));

            var with9006 = new HotkeySettings { Hotkeys = new List<HotkeyData>(beforeQuickTranslateRepeat.Hotkeys) { Key(9006, true, true, 'Q') } };
            merged = HotkeySettingsManager.MergeMissingDefaults(with9006, defaults);
            Assert.AreEqual('E', merged.Hotkeys.Single(h => h.Id == 9007).SelectedKey);
            Assert.AreEqual(defaults.Hotkeys.Count, merged.Hotkeys.Count);
        }

        [TestMethod]
        public void ABindingWindowsRefused_IsNotNamedInCopy()
        {
            try
            {
                HotkeySettingsManager.ReportRegistration(HotkeySettingsManager.FixLineHotkeyId, registered: false);
                Assert.IsNull(HotkeySettingsManager.DescribeBinding(HotkeySettingsManager.FixLineHotkeyId),
                    "another app owns the combination: point at the tray entry instead");
            }
            finally
            {
                HotkeySettingsManager.ReportRegistration(HotkeySettingsManager.FixLineHotkeyId, registered: true);
            }
        }

        [TestMethod]
        public void NothingToFix_NamesTheShortcut_OrPointsAtTheTray()
        {
            var asked = new List<string>();
            string Localize(string key, string fallback)
            {
                asked.Add(key);
                return key == "fix_Nothing_Body" ? "Gdy napis będzie na ekranie, wciśnij {0}." : fallback;
            }

            var bound = GI_Subtitles.Views.MainWindow.NothingToFixText("Ctrl+Shift+E", Localize);
            Assert.AreEqual("No line to fix yet", bound.Title);
            Assert.AreEqual("Gdy napis będzie na ekranie, wciśnij Ctrl+Shift+E.", bound.Body, "the shortcut is filled in");

            var unbound = GI_Subtitles.Views.MainWindow.NothingToFixText(null, Localize);
            StringAssert.Contains(unbound.Body, "right-click the Kaption icon", "no shortcut to name: the tray entry instead");
            Assert.IsFalse(unbound.Body.Contains("{0}"));
            CollectionAssert.AreEqual(new[] { "fix_Nothing_Title", "fix_Nothing_Body", "fix_Nothing_Title", "fix_Nothing_Body_Tray" }, asked);
        }

        [TestMethod]
        public void Merge_LeavesCtrlShiftE_ToAUserWhoAlreadyUsesIt()
        {
            var saved = new HotkeySettings
            {
                Hotkeys = new List<HotkeyData> { Key(9000, true, true, 'S'), Key(9001, true, true, 'e') },
            };
            var merged = HotkeySettingsManager.MergeMissingDefaults(saved, HotkeySettingsManager.GetDefaultSettings());
            Assert.IsFalse(merged.Hotkeys.Any(h => h.Id == 9007), "the user's own binding wins; 9007 stays unbound");
            Assert.AreEqual('e', merged.Hotkeys.Single(h => h.Id == 9001).SelectedKey);
            Assert.IsTrue(merged.Hotkeys.Any(h => h.Id == 9006), "other new defaults still arrive");
        }
    }
}
