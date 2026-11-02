using System.Windows.Controls;
using GI_Subtitles.Services.Automation;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// Review fixes: the Start card says why auto-start waits (and offers the gate's own fix), and the setup guide keeps
    /// a saved dialogue area.
    /// </summary>
    [TestClass]
    public class AutoStartBlockedTests
    {
        private static StartCardInputs Ready(StartGate blocked, bool waiting = false) => new StartCardInputs
        {
            Reader = ReaderState.Ready, PackOnDisk = true, AutoStartBlockedBy = blocked, WaitingForGame = waiting,
        };

        [TestMethod]
        [DataRow(StartGate.DisplayModeTip)]
        [DataRow(StartGate.OverlayOverlap)]
        [DataRow(StartGate.Region)]
        [DataRow(StartGate.Session)]
        public void AClosedGate_PutsTheCardOnHold_WithOneFix(StartGate gate)
        {
            var s = StartCardModel.Compute(Ready(gate, waiting: true));
            Assert.AreEqual(StartCardKind.AutoStartBlocked, s.Kind, "not 'ready' and not 'waiting for the game'");
            Assert.AreEqual(StartCardAction.ResolveGate, s.Action);
            Assert.AreEqual(gate, s.Gate);
            Assert.IsFalse(StartCardModel.IsGradientAction(s.Action), "a fix is a quiet outline");
        }

        [TestMethod]
        public void TheHoldClears_WhenTheGateOpens()
        {
            Assert.AreEqual(StartCardKind.WaitingForGame, StartCardModel.Compute(Ready(StartGate.None, waiting: true)).Kind);
            Assert.AreEqual(StartCardKind.WaitingForGame, StartCardModel.Compute(Ready(StartGate.GameRunning, waiting: true)).Kind,
                "the game itself is 'Czekam na grę', not a hold");
            Assert.AreEqual(StartCardKind.Stopped, StartCardModel.Compute(Ready(StartGate.None)).Kind);
        }

        [TestMethod]
        public void TranslatingAndProgress_StillWin()
        {
            Assert.AreEqual(StartCardKind.Translating,
                StartCardModel.Compute(new StartCardInputs { Translating = true, Reader = ReaderState.Ready, AutoStartBlockedBy = StartGate.OverlayOverlap }).Kind);
            Assert.AreEqual(StartCardKind.StartingReader,
                StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Loading, AutoStartBlockedBy = StartGate.Engine }).Kind,
                "the reader starting says more than 'on hold: translator still loading'");
        }

        [TestMethod]
        [DataRow(StartGate.DisplayModeTip, "Ustaw grę w trybie Bez ramki albo Okno", "Pokaż wskazówkę")]
        [DataRow(StartGate.OverlayOverlap, "Napisy zasłaniają dialog gry", "Popraw obszar")]
        public void TheCardSaysWhy_InTheGatesOwnWords(StartGate gate, string reason, string action)
        {
            RunSta(() =>
            {
                var host = LoadLooseWindow("Views/SettingsWindow.xaml", "pl-PL", out var window, "Dark");
                string L(string k, string f) => host.TryFindResource(k) as string ?? f;
                var inputs = Ready(gate, waiting: true);
                var state = StartCardModel.Compute(inputs);
                SettingsWindow.ApplyStartCardTo(window.FindName, state, inputs, L, new System.Globalization.CultureInfo("pl-PL"));
                SettingsWindow.ApplyStateChipTo(window.FindName, state, L);
                Assert.AreEqual("Auto-start wstrzymany", ((TextBlock)window.FindName("StateCardTitle")).Text);
                Assert.AreEqual(reason, ((TextBlock)window.FindName("StateCardDetail")).Text);
                Assert.AreEqual(action, AccessKeyTextConverter.Strip((string)((Button)window.FindName("DashToggleOcrButton")).Content));
                Assert.AreEqual("Auto-start wstrzymany", ((TextBlock)window.FindName("DashOcrDetailStatus")).Text);
            });
        }

        [TestMethod]
        public void RerunSetup_KeepsASavedRegion_UnlessAsked()
        {
            Assert.IsFalse(OnboardingFlow.ShouldAutoDetectRegion(savedRegionValid: true, userAsked: false),
                "the setup guide opened with the game running must not replace a saved area");
            Assert.IsTrue(OnboardingFlow.ShouldAutoDetectRegion(savedRegionValid: true, userAsked: true), "Wykryj automatycznie replaces it");
            Assert.IsTrue(OnboardingFlow.ShouldAutoDetectRegion(savedRegionValid: false, userAsked: false), "no area yet: detect one");

            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "GI-Subtitles", "Views", "OnboardingWindow.xaml.cs"));
            int detect = code.IndexOf("private async Task DetectAsync()", System.StringComparison.Ordinal);
            int rule = code.IndexOf("OnboardingFlow.ShouldAutoDetectRegion(SavedRegionValid(), userAsked: false)", detect, System.StringComparison.Ordinal);
            int auto = code.IndexOf("AutoDetectRegionAsync(id)", detect, System.StringComparison.Ordinal);
            Assert.IsTrue(detect > 0 && rule > detect && auto > rule, "finding the game checks the saved area before detecting one");
            StringAssert.Contains(code, "onb_Region_Saved", "a kept area says it was saved");
        }
    }
}
