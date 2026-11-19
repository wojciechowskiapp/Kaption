using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Settings › Subtitle style › the Ctrl+Q popup card, through its view model and no window: what it shows from the
    /// saved values, what it writes and in which shape. WindowConstructionTests checks the same card in the real window.
    /// </summary>
    [TestClass]
    public class QuickTranslateCardViewModelTests
    {
        private static QuickTranslateCardViewModel Build(MemorySettingsStore store) => new(store);

        [TestMethod]
        public void NothingSaved_ShowsTheDefaults_AndWritesNothing()
        {
            var store = new MemorySettingsStore();
            var vm = Build(store);

            Assert.AreEqual(QuickTranslateCloseMode.Auto, vm.Mode);
            Assert.IsTrue(vm.IsAuto);
            Assert.IsFalse(vm.IsTimed);
            Assert.IsFalse(vm.IsManual);
            Assert.AreEqual(QuickTranslateSettings.DefaultSeconds, vm.Seconds);
            Assert.AreEqual(QuickTranslateSettings.DefaultFontSize, vm.FontSize);
            Assert.IsFalse(vm.IsSecondsRowVisible, "the seconds row belongs to the timed mode");
            Assert.IsTrue(vm.IsPreviewTimerVisible, "the popup counts down when it closes by itself");
            Assert.AreEqual(0, store.Writes.Count, "showing a default is not a choice");
        }

        [TestMethod]
        public void SavedValues_AreShown_AndLoadingWritesNothing()
        {
            var store = new MemorySettingsStore()
                .With(QuickTranslateSettings.CloseModeKey, "Timed")
                .With(QuickTranslateSettings.SecondsKey, 30)
                .With(QuickTranslateSettings.FontSizeKey, 20);
            var vm = Build(store);

            Assert.IsTrue(vm.IsTimed);
            Assert.AreEqual(30, vm.Seconds);
            Assert.AreEqual(20, vm.FontSize);
            Assert.IsTrue(vm.IsSecondsRowVisible);
            Assert.AreEqual(0, store.Writes.Count);
        }

        [TestMethod]
        public void HandEditedValues_AreClamped_OrFallBack_WithoutRewritingThem()
        {
            var store = new MemorySettingsStore()
                .With(QuickTranslateSettings.CloseModeKey, "sometimes")
                .With(QuickTranslateSettings.SecondsKey, 500)
                .With(QuickTranslateSettings.FontSizeKey, 3);
            var vm = Build(store);

            Assert.AreEqual(QuickTranslateCloseMode.Auto, vm.Mode);
            Assert.AreEqual(QuickTranslateSettings.MaxSeconds, vm.Seconds);
            Assert.AreEqual(QuickTranslateSettings.MinFontSize, vm.FontSize);
            Assert.AreEqual(0, store.Writes.Count);
        }

        [TestMethod]
        public void AStoreThatThrows_GivesTheDefaults()
        {
            var store = new MemorySettingsStore().With(QuickTranslateSettings.SecondsKey, new InvalidCastException("hand-edited"));
            var vm = Build(store);

            Assert.AreEqual(QuickTranslateCloseMode.Auto, vm.Mode);
            Assert.AreEqual(QuickTranslateSettings.DefaultSeconds, vm.Seconds);
            Assert.AreEqual(QuickTranslateSettings.DefaultFontSize, vm.FontSize);
        }

        [TestMethod]
        public void PickingTimed_StoresTheModeAsItsName_AndShowsTheSecondsRow()
        {
            var store = new MemorySettingsStore();
            var vm = Build(store);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.IsTimed = true;

            Assert.AreEqual(1, store.Writes.Count);
            Assert.AreEqual(QuickTranslateSettings.CloseModeKey, store.Writes[0].Key);
            Assert.IsInstanceOfType(store.Writes[0].Value, typeof(string), "2.6.2 and older read the mode as a string");
            Assert.AreEqual("Timed", store.Writes[0].Value);
            Assert.IsTrue(vm.IsSecondsRowVisible);
            Assert.IsTrue(vm.IsPreviewTimerVisible);
            CollectionAssert.IsSubsetOf(
                new[] { nameof(vm.Mode), nameof(vm.IsAuto), nameof(vm.IsTimed), nameof(vm.IsManual),
                        nameof(vm.IsSecondsRowVisible), nameof(vm.IsPreviewTimerVisible) },
                raised, "every control that follows the mode hears about it");
        }

        [TestMethod]
        public void PickingManual_HidesTheSecondsRow_AndThePreviewCountdown()
        {
            var store = new MemorySettingsStore().With(QuickTranslateSettings.CloseModeKey, "Timed");
            var vm = Build(store);

            vm.IsManual = true;

            Assert.AreEqual(QuickTranslateCloseMode.Manual, vm.Mode);
            Assert.IsFalse(vm.IsSecondsRowVisible);
            Assert.IsFalse(vm.IsPreviewTimerVisible, "a popup that waits for the player shows no countdown");
            Assert.AreEqual(("QuickTranslateCloseMode", (object)"Manual"), store.Writes.Single());
        }

        [TestMethod]
        public void TheRadioGroupUncheckingTheOldChoice_WritesNothingMore()
        {
            // WPF unchecks the old radio button of the group and its two-way binding writes false back: one change,
            // one write, whichever order the two arrive in.
            var store = new MemorySettingsStore();
            var vm = Build(store);

            vm.IsTimed = true;
            vm.IsAuto = false;
            vm.IsManual = false;

            Assert.AreEqual(QuickTranslateCloseMode.Timed, vm.Mode);
            Assert.AreEqual(1, store.Writes.Count);

            vm.IsTimed = false; // a false alone never picks another mode
            Assert.AreEqual(QuickTranslateCloseMode.Timed, vm.Mode);
            Assert.AreEqual(1, store.Writes.Count);
        }

        [TestMethod]
        public void TheSliders_StoreWholeNumbers_ThatTheNextPopupReadsBack()
        {
            var store = new MemorySettingsStore();
            var vm = Build(store);

            vm.Seconds = 45;
            vm.FontSize = 22;
            vm.FontSize = 22; // the same value again is no change

            CollectionAssert.AreEqual(
                new[] { (QuickTranslateSettings.SecondsKey, (object)45), (QuickTranslateSettings.FontSizeKey, (object)22) },
                store.Writes);
            Assert.IsTrue(store.Writes.All(w => w.Value is int), "the sliders write ints, as before");
            var next = QuickTranslateSettings.Load(store);
            Assert.AreEqual(45, next.Seconds);
            Assert.AreEqual(22, next.FontSize);
        }
    }
}
