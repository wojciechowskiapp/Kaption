using System.Globalization;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The Start dashboard's pure parts: the state card's priority order, pack freshness, plurals, titles.</summary>
    [TestClass]
    public class DashboardModelTests
    {
        private static StartCardInputs Ready(bool translating = false) => new StartCardInputs
        {
            Translating = translating, Reader = ReaderState.Ready, PackOnDisk = true,
        };

        [TestMethod]
        public void IdleAndReady_IsStoppedWithStart()
        {
            var s = StartCardModel.Compute(Ready());
            Assert.AreEqual(StartCardKind.Stopped, s.Kind);
            Assert.AreEqual(StartCardAction.Start, s.Action);
        }

        [TestMethod]
        public void Translating_OffersStop_AndBeatsBackgroundWork()
        {
            var s = StartCardModel.Compute(new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, Downloading = true, IndexBuilding = true,
            });
            Assert.AreEqual(StartCardKind.Translating, s.Kind);
            Assert.AreEqual(StartCardAction.Stop, s.Action);
        }

        [TestMethod]
        public void DeviceLimit_WinsOverEverything()
        {
            var s = StartCardModel.Compute(new StartCardInputs { DeviceLimit = true, Translating = true, Downloading = true });
            Assert.AreEqual(StartCardKind.DeviceLimit, s.Kind);
            Assert.AreEqual(StartCardAction.OpenAccount, s.Action);
        }

        [TestMethod]
        public void Downloading_HasNoActionAndShowsProgress()
        {
            var s = StartCardModel.Compute(new StartCardInputs { Downloading = true, DownloadFraction = 0.42, Reader = ReaderState.Loading });
            Assert.AreEqual(StartCardKind.Downloading, s.Kind);
            Assert.AreEqual(StartCardAction.None, s.Action);
            Assert.IsTrue(s.ShowProgress);
            Assert.AreEqual(0.42, s.Progress, 1e-9);
        }

        [TestMethod]
        public void Downloading_WithoutNumbers_HidesTheBar()
        {
            var s = StartCardModel.Compute(new StartCardInputs { Downloading = true });
            Assert.IsFalse(s.ShowProgress);
        }

        [TestMethod]
        public void OfflineWithoutPack_AsksToRetry_OfflineWithPack_IsANote()
        {
            var noPack = StartCardModel.Compute(new StartCardInputs { Offline = true, PackOnDisk = false, Reader = ReaderState.Ready });
            Assert.AreEqual(StartCardKind.Offline, noPack.Kind);
            Assert.AreEqual(StartCardAction.Retry, noPack.Action);

            var withPack = StartCardModel.Compute(new StartCardInputs { Offline = true, PackOnDisk = true, Reader = ReaderState.Ready });
            Assert.AreEqual(StartCardKind.Stopped, withPack.Kind);
            Assert.IsTrue(withPack.ShowOfflineNote);
        }

        [TestMethod]
        public void IndexBuild_ThenReader_ThenReady()
        {
            Assert.AreEqual(StartCardKind.BuildingIndex,
                StartCardModel.Compute(new StartCardInputs { IndexBuilding = true, Reader = ReaderState.Loading, PackOnDisk = true }).Kind);
            Assert.AreEqual(StartCardKind.StartingReader,
                StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Loading, PackOnDisk = true }).Kind);
            Assert.AreEqual(StartCardKind.Stopped, StartCardModel.Compute(Ready()).Kind);
        }

        [TestMethod]
        public void ReaderFailed_OffersRetry()
        {
            var s = StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Failed, PackOnDisk = true });
            Assert.AreEqual(StartCardKind.ReaderFailed, s.Kind);
            Assert.AreEqual(StartCardAction.RetryReader, s.Action);
        }

        [TestMethod]
        public void ReaderUnavailable_OffersRestartOnly()
        {
            var s = StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Unavailable, PackOnDisk = true, ReaderErrorCode = "0x8007045A" });
            Assert.AreEqual(StartCardKind.ReaderUnavailable, s.Kind);
            Assert.AreEqual(StartCardAction.RestartApp, s.Action, "a failed native load is cached for the process; retrying cannot help");
            Assert.IsFalse(StartCardModel.IsGradientAction(s.Action));
        }

        [TestMethod]
        public void PackMissing_PointsAtThePacks()
        {
            var s = StartCardModel.Compute(new StartCardInputs { PackMissing = true, Reader = ReaderState.Ready });
            Assert.AreEqual(StartCardKind.PackMissing, s.Kind);
            Assert.AreEqual(StartCardAction.OpenPacks, s.Action);
        }

        [TestMethod]
        public void ABetaGamesFirstPackOnTheWay_IsNoProblem_AndOffersNoDownload()
        {
            var s = StartCardModel.Compute(new StartCardInputs { PackMissing = true, PackComing = true, Reader = ReaderState.Ready });
            Assert.AreEqual(StartCardKind.PackComing, s.Kind);
            Assert.AreEqual(StartCardAction.None, s.Action, "there is no pack to download yet");
            Assert.AreEqual(GI_Subtitles.Core.UI.TrayState.Idle, GI_Subtitles.Views.SettingsWindow.TrayStateFor(s.Kind), "not a problem in the tray");
            Assert.AreEqual(StartCardKind.Stopped, StartCardModel.Compute(new StartCardInputs { PackComing = true, Reader = ReaderState.Ready, PackOnDisk = true }).Kind,
                "a pack on disk wins: the listing said nothing about it");
        }

        [TestMethod]
        public void WaitingForGame_OffersStartAnyway_AndCpuNoteOnlyWhenReaderReady()
        {
            var s = StartCardModel.Compute(new StartCardInputs { WaitingForGame = true, Reader = ReaderState.Ready, ReaderOnCpu = true, PackOnDisk = true });
            Assert.AreEqual(StartCardKind.WaitingForGame, s.Kind);
            Assert.AreEqual(StartCardAction.StartAnyway, s.Action);
            Assert.IsTrue(s.ShowCpuNote);
            Assert.IsFalse(StartCardModel.Compute(new StartCardInputs { Reader = ReaderState.Loading, ReaderOnCpu = true }).ShowCpuNote);
        }

        [TestMethod]
        [DataRow(StartCardAction.Start, true)]
        [DataRow(StartCardAction.Stop, true)]
        [DataRow(StartCardAction.StartAnyway, false)]
        [DataRow(StartCardAction.Retry, false)]
        [DataRow(StartCardAction.RetryReader, false)]
        [DataRow(StartCardAction.RestartApp, false)]
        [DataRow(StartCardAction.OpenAccount, false)]
        [DataRow(StartCardAction.OpenPacks, false)]
        public void OnlyStartAndStopWearTheGradient(StartCardAction action, bool gradient)
        {
            Assert.AreEqual(gradient, StartCardModel.IsGradientAction(action), "waiting for the game is a quiet outline, not the hero button");
        }

        private static string L(string key, string fallback) => fallback;

        [TestMethod]
        public void PackRows_AmberOnlyWhenTheGameInUseHasNoPack()
        {
            var en = new CultureInfo("en-US");
            var upToDate = GI_Subtitles.Views.SettingsWindow.PackRowStatus(PackFreshness.UpToDate, false, true, "7.1.0-r8", "7.1.0-r8", L, en);
            Assert.AreEqual(("SuccessBrush", false, "7.1.0-r8"), (upToDate.BrushKey, upToDate.IsDownload, upToDate.SubLine));

            var newer = GI_Subtitles.Views.SettingsWindow.PackRowStatus(PackFreshness.UpdateAvailable, false, false, "4.6.0-r3", "4.6.0-r5", L, en);
            Assert.AreEqual(("Download 4.6.0-r5", "AccentTextBrush", true), (newer.Text, newer.BrushKey, newer.IsDownload), "a newer pack is a quiet link");

            var otherGame = GI_Subtitles.Views.SettingsWindow.PackRowStatus(PackFreshness.NotInstalled, false, false, null, "3.3.0-r3", L, en);
            Assert.AreEqual(("downloads when you switch", string.Empty), (otherGame.Text, otherGame.SubLine), "another game's pack is not a problem");
            Assert.AreEqual("TextTertiaryBrush", otherGame.BrushKey, "said quietly, on the same line");

            var inUse = GI_Subtitles.Views.SettingsWindow.PackRowStatus(PackFreshness.NotInstalled, false, true, null, "7.1.0-r8", L, en);
            Assert.AreEqual("WarnBrush", inUse.BrushKey, "the game Kaption reads has no pack: that one is a problem");

            var busy = GI_Subtitles.Views.SettingsWindow.PackRowStatus(PackFreshness.NotInstalled, true, false, null, "3.3.0-r3", L, en);
            Assert.AreEqual("InfoBrush", busy.BrushKey);
        }

        [TestMethod]
        [DataRow("7.1.0-r5", "7.1.0-r5", PackFreshness.UpToDate)]
        [DataRow("4.6.0-r3", "4.6.0-r5", PackFreshness.UpdateAvailable)]
        [DataRow("3.3.0-r2", "3.3.0-r3", PackFreshness.UpdateAvailable)]
        [DataRow("7.0.0-r9", "7.1.0-r1", PackFreshness.UpdateAvailable)]
        [DataRow("7.1.0-r5", "7.1.0-r4", PackFreshness.UpToDate)]
        [DataRow("7.1-r2", "7.1.0-r2", PackFreshness.UpToDate)]
        [DataRow("custom", "custom", PackFreshness.UpToDate)]
        [DataRow("custom", "other", PackFreshness.UpdateAvailable)]
        [DataRow(null, "7.1.0-r5", PackFreshness.NotInstalled)]
        [DataRow("7.1.0-r5", null, PackFreshness.Unknown)]
        public void PackFreshnessComparesGameVersionThenRevision(string installed, string latest, PackFreshness expected)
        {
            Assert.AreEqual(expected, PackStatus.Compare(installed, latest));
        }

        [TestMethod]
        [DataRow(100_663_296L, "pl-PL", "96 MB")]
        [DataRow(5_242_880L, "en-US", "5.0 MB")]
        [DataRow(1_288_490_189L, "pl-PL", "1,2 GB")]
        public void SizesReadLikeTheUiCulture(long bytes, string culture, string expected)
        {
            Assert.AreEqual(expected, PackStatus.FormatSize(bytes, new CultureInfo(culture)));
        }

        [TestMethod]
        [DataRow(1L, "1 linia")]
        [DataRow(2L, "2 linie")]
        [DataRow(4L, "4 linie")]
        [DataRow(5L, "5 linii")]
        [DataRow(12L, "12 linii")]
        [DataRow(14L, "14 linii")]
        [DataRow(22L, "22 linie")]
        [DataRow(37L, "37 linii")]
        [DataRow(0L, "0 linii")]
        public void PolishPluralsFollowTheGrammar(long n, string expected)
        {
            Assert.AreEqual(expected, Plural.Format("{0:N0} linia|{0:N0} linie|{0:N0} linii", n, new CultureInfo("pl-PL")));
        }

        [TestMethod]
        [DataRow(1L, "1 line")]
        [DataRow(2L, "2 lines")]
        [DataRow(1234L, "1,234 lines")]
        public void EnglishPluralsUseTwoForms(long n, string expected)
        {
            Assert.AreEqual(expected, Plural.Format("{0:N0} line|{0:N0} lines|{0:N0} lines", n, new CultureInfo("en-US")));
        }

        [TestMethod]
        public void ReleaseTitlesMatchTheSite()
        {
            const string nv = "{0} {1} po polsku: {2}", nvBare = "{0} {1} po polsku: co nowego", rev = "{0} {1} po polsku: poprawki napisów";
            Assert.AreEqual("Zenless Zone Zero 3.3 po polsku: Tajemnica Shrouded Reef",
                ReleaseNoteTitle.Build("Zenless Zone Zero", "3.3", "new_version", "Tajemnica Shrouded Reef", nv, nvBare, rev));
            Assert.AreEqual("Genshin Impact 7.1 po polsku: co nowego",
                ReleaseNoteTitle.Build("Genshin Impact", "7.1", "new_version", " ", nv, nvBare, rev));
            Assert.AreEqual("Honkai: Star Rail 4.6 po polsku: poprawki napisów",
                ReleaseNoteTitle.Build("Honkai: Star Rail", "4.6", "revision", "ignored", nv, nvBare, rev));
        }
    }
}
