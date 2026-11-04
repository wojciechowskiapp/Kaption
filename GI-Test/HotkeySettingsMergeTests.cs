using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Models;
using GI_Subtitles.Services.QuickTranslate;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class HotkeySettingsMergeTests
    {
        private static HotkeyData Key(int id, bool ctrl, bool shift, char key, bool alt = false) =>
            new HotkeyData { Id = id, IsCtrl = ctrl, IsShift = shift, IsAlt = alt, SelectedKey = key, Description = "d" + id };

        // What a hotkeySettings.xml saved before 9006 existed looks like.
        private static HotkeySettings OldSave(params HotkeyData[] overrides)
        {
            var list = new List<HotkeyData>
            {
                Key(9000, true, true, 'S'), Key(9001, true, true, 'R'), Key(9002, true, true, 'H'),
                Key(9003, true, true, 'D'), Key(9004, true, false, '1'), Key(9005, true, false, 'Q'),
            };
            foreach (var o in overrides)
            {
                list.RemoveAll(h => h.Id == o.Id);
                list.Add(o);
            }
            return new HotkeySettings { Hotkeys = list };
        }

        [TestMethod]
        public void Defaults_Include9006_CtrlShiftQ()
        {
            var d = HotkeySettingsManager.GetDefaultSettings().Hotkeys.Single(h => h.Id == 9006);
            Assert.IsTrue(d.IsCtrl && d.IsShift && !d.IsAlt);
            Assert.AreEqual('Q', d.SelectedKey);
        }

        [TestMethod]
        public void Merge_AddsMissing9006()
        {
            var merged = HotkeySettingsManager.MergeMissingDefaults(OldSave(), HotkeySettingsManager.GetDefaultSettings());
            var added = merged.Hotkeys.Single(h => h.Id == 9006);
            Assert.AreEqual('Q', added.SelectedKey);
            Assert.AreEqual(HotkeySettingsManager.GetDefaultSettings().Hotkeys.Count, merged.Hotkeys.Count, "every later default arrives too");
        }

        [TestMethod]
        public void Merge_KeepsUserBindings()
        {
            var merged = HotkeySettingsManager.MergeMissingDefaults(
                OldSave(Key(9005, false, false, 'T', alt: true)), HotkeySettingsManager.GetDefaultSettings());
            var qt = merged.Hotkeys.Single(h => h.Id == 9005);
            Assert.IsTrue(qt.IsAlt);
            Assert.AreEqual('T', qt.SelectedKey);
        }

        [TestMethod]
        public void Merge_SkipsWhenComboTaken()
        {
            var merged = HotkeySettingsManager.MergeMissingDefaults(
                OldSave(Key(9001, true, true, 'Q')), HotkeySettingsManager.GetDefaultSettings());
            Assert.IsFalse(merged.Hotkeys.Any(h => h.Id == 9006));
        }

        [TestMethod]
        public void Merge_ComboCompareIgnoresCase()
        {
            var merged = HotkeySettingsManager.MergeMissingDefaults(
                OldSave(Key(9001, true, true, 'q')), HotkeySettingsManager.GetDefaultSettings());
            Assert.IsFalse(merged.Hotkeys.Any(h => h.Id == 9006));
        }

        [TestMethod]
        public void Merge_Idempotent()
        {
            var defaults = HotkeySettingsManager.GetDefaultSettings();
            var once = HotkeySettingsManager.MergeMissingDefaults(OldSave(), defaults);
            var twice = HotkeySettingsManager.MergeMissingDefaults(once, defaults);
            Assert.AreEqual(once.Hotkeys.Count, twice.Hotkeys.Count);
        }

        [TestMethod]
        public void Merge_NullLoaded_ReturnsDefaults()
        {
            var merged = HotkeySettingsManager.MergeMissingDefaults(null, HotkeySettingsManager.GetDefaultSettings());
            Assert.AreEqual(HotkeySettingsManager.GetDefaultSettings().Hotkeys.Count, merged.Hotkeys.Count);
        }

        private static readonly Rectangle Screen = new Rectangle(0, 0, 1920, 1080);

        [TestMethod]
        public void LastArea_RoundTrips()
        {
            var area = new Rectangle(100, 200, 640, 480);
            Assert.IsTrue(QuickTranslateArea.TryParse(QuickTranslateArea.Format(area), Screen, out var parsed));
            Assert.AreEqual(area, parsed);
        }

        [TestMethod]
        public void LastArea_ParseRejectsGarbage()
        {
            foreach (var bad in new[] { null, "", "1,2,3", "a,b,c,d", "1,2,3,4,5", "10,10,-5,20", "10,10,3,3" })
                Assert.IsFalse(QuickTranslateArea.TryParse(bad, Screen, out _), bad ?? "null");
        }

        [TestMethod]
        public void LastArea_RejectsOffScreen()
        {
            // Saved on a monitor that is no longer connected.
            Assert.IsFalse(QuickTranslateArea.TryParse("2000,100,300,200", Screen, out _));
            Assert.IsFalse(QuickTranslateArea.TryParse("1800,100,300,200", Screen, out _));
        }
    }
}
