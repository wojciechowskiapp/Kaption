using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
// GI_Test has a Config of its own; the app's is the one the setting lives in.
using AppConfig = GI_Subtitles.Core.Config.Config;
using Rectangle = System.Drawing.Rectangle;

namespace GI_Test
{
    /// <summary>
    /// "Show subtitles in recordings and streams": when the overlay asks Windows to hide it, when the app blacks its own
    /// overlay out of the frames it reads, what the line under the setting says, and the optional Kaption mark. No test
    /// touches a real window: the Win32 call stays behind MainWindow.TryExcludeFromCapture.
    /// </summary>
    [TestClass]
    public class RecordingModeTests
    {
        /// <summary>Everything "restore defaults" clears on the look page (OverlayStyle.ResetConfig).</summary>
        private static readonly string[] LookKeys =
        {
            OverlayStyle.SizeKey, OverlayStyle.OpacityKey, OverlayStyle.OutlineKey, OverlayStyle.LetterSpacingKey, OverlayStyle.AutoDarkenKey,
            OverlayStyle.MarkKey, OverlayStyle.FontKey, OverlayStyle.LegacyFontKey, OverlayStyle.PadKey,
            "MaxOverlayWidth", "MaxOverlayHeight", "AutoShrinkText",
        };

        /// <summary>Runs <paramref name="test"/> with the keys cleared, then puts back exactly what was saved, whatever its type.</summary>
        private static void WithKeysCleared(string[] keys, Action test)
        {
            var saved = keys.Where(AppConfig.Has).ToDictionary(k => k, k => AppConfig.Get<System.Text.Json.Nodes.JsonNode>(k, null)?.ToJsonString());
            foreach (string k in keys) AppConfig.Remove(k);
            try { test(); }
            finally
            {
                foreach (string k in keys) AppConfig.Remove(k);
                foreach (var kv in saved)
                    if (kv.Value != null) AppConfig.Set(kv.Key, System.Text.Json.Nodes.JsonNode.Parse(kv.Value));
            }
        }

        // ── The setting ─────────────────────────────────────────────────────────

        [TestMethod]
        public void AnAbsentKey_MeansTheNamedDefault_AndNothingIsWritten()
        {
            WithKeysCleared(new[] { CaptureVisibility.ShowInRecordingsKey }, () =>
            {
                Assert.AreEqual(CaptureVisibility.DefaultShowInRecordings, CaptureVisibility.ShowInRecordings);
                Assert.IsFalse(AppConfig.Has(CaptureVisibility.ShowInRecordingsKey), "reading the setting must not save it");
                Assert.AreEqual(CaptureVisibility.DefaultShowInRecordings, CaptureVisibility.Resolve(null));
            });
        }

        [TestMethod]
        public void ASavedChoice_Wins_BothWays()
        {
            WithKeysCleared(new[] { CaptureVisibility.ShowInRecordingsKey }, () =>
            {
                AppConfig.Set(CaptureVisibility.ShowInRecordingsKey, true);
                Assert.IsTrue(CaptureVisibility.ShowInRecordings);
                AppConfig.Set(CaptureVisibility.ShowInRecordingsKey, false);
                Assert.IsFalse(CaptureVisibility.ShowInRecordings);
            });
            Assert.IsTrue(CaptureVisibility.Resolve(true));
            Assert.IsFalse(CaptureVisibility.Resolve(false));
        }

        [TestMethod]
        public void TheOverlayAsksWindowsToHideIt_OnlyWhileTheSettingIsOff()
        {
            Assert.IsTrue(CaptureVisibility.AsksWindowsToHide(showInRecordings: false));
            Assert.IsFalse(CaptureVisibility.AsksWindowsToHide(showInRecordings: true));
        }

        // (setting, what Windows answered, the backend honours hiding) -> the app masks its own overlay
        [TestMethod]
        [DataRow(true, WindowsHideAnswer.NotAsked, true, true, DisplayName = "on, DXGI: masked")]
        [DataRow(true, WindowsHideAnswer.NotAsked, false, true, DisplayName = "on, GDI: masked")]
        [DataRow(true, WindowsHideAnswer.Hidden, true, true, DisplayName = "on, a stale 'hidden' answer: still masked")]
        [DataRow(false, WindowsHideAnswer.Refused, true, true, DisplayName = "off, Windows refused, DXGI: masked")]
        [DataRow(false, WindowsHideAnswer.Refused, false, true, DisplayName = "off, Windows refused, GDI: masked")]
        [DataRow(false, WindowsHideAnswer.NotAsked, true, true, DisplayName = "off, not asked yet: masked")]
        [DataRow(false, WindowsHideAnswer.Hidden, false, true, DisplayName = "off, hidden, GDI: masked (GDI promises nothing)")]
        [DataRow(false, WindowsHideAnswer.Hidden, true, false, DisplayName = "off, hidden, DXGI: the only read left unmasked")]
        [DataRow(true, WindowsHideAnswer.StillHidden, true, true, DisplayName = "on, Windows kept it hidden: masked all the same")]
        public void HideOrMask(bool show, WindowsHideAnswer answer, bool backendHonoursHiding, bool masked)
        {
            Assert.AreEqual(masked, CaptureVisibility.MasksOverlay(show, answer, backendHonoursHiding));
        }

        [TestMethod]
        public void TheOverlayIsNeverBothUnhiddenAndUnmasked()
        {
            foreach (bool show in new[] { false, true })
            foreach (WindowsHideAnswer answer in Enum.GetValues(typeof(WindowsHideAnswer)))
            foreach (bool honours in new[] { false, true })
            {
                bool inFrame = show || answer != WindowsHideAnswer.Hidden || !honours;
                if (inFrame)
                    Assert.IsTrue(CaptureVisibility.MasksOverlay(show, answer, honours), $"{show}/{answer}/{honours}: in the frame, so masked");
            }
        }

        // ── The line under the setting ──────────────────────────────────────────

        [TestMethod]
        [DataRow(true, WindowsHideAnswer.NotAsked, CaptureStatus.Shown)]
        [DataRow(true, WindowsHideAnswer.Refused, CaptureStatus.Shown)]
        [DataRow(false, WindowsHideAnswer.Hidden, CaptureStatus.Hidden)]
        [DataRow(false, WindowsHideAnswer.Refused, CaptureStatus.ShownWindowsRefused)]
        [DataRow(false, WindowsHideAnswer.NotAsked, CaptureStatus.Unknown)]
        [DataRow(true, WindowsHideAnswer.StillHidden, CaptureStatus.HiddenWindowsRefused)]
        [DataRow(true, WindowsHideAnswer.Hidden, CaptureStatus.Shown)]
        public void Status_FromTheSettingAndWindowsAnswer(bool show, WindowsHideAnswer answer, CaptureStatus expected)
        {
            Assert.AreEqual(expected, CaptureVisibility.Status(show, answer));
        }

        // Windows refusing to put the overlay back matters only when it was hidden before.
        [TestMethod]
        [DataRow(true, WindowsHideAnswer.Hidden, WindowsHideAnswer.NotAsked)]
        [DataRow(true, WindowsHideAnswer.Refused, WindowsHideAnswer.NotAsked)]
        [DataRow(true, WindowsHideAnswer.StillHidden, WindowsHideAnswer.NotAsked)]
        [DataRow(false, WindowsHideAnswer.Hidden, WindowsHideAnswer.StillHidden)]
        [DataRow(false, WindowsHideAnswer.StillHidden, WindowsHideAnswer.StillHidden)]
        [DataRow(false, WindowsHideAnswer.Refused, WindowsHideAnswer.NotAsked)]
        [DataRow(false, WindowsHideAnswer.NotAsked, WindowsHideAnswer.NotAsked)]
        public void AfterAskingToShow_AFailedUndoIsNotReportedAsShown(bool windowsAccepted, WindowsHideAnswer before, WindowsHideAnswer after)
        {
            Assert.AreEqual(after, CaptureVisibility.AfterAskingToShow(windowsAccepted, before));
            if (after == WindowsHideAnswer.StillHidden)
            {
                Assert.AreEqual(CaptureStatus.HiddenWindowsRefused, CaptureVisibility.Status(true, after), "the line must not say the subtitles are shown");
                StringAssert.Contains(CaptureVisibility.StatusFallback(CaptureStatus.HiddenWindowsRefused), "Restart");
            }
        }

        [TestMethod]
        public void EveryStatus_HasItsSentence_InEnglishAndPolish()
        {
            RunSta(() =>
            {
                foreach (string culture in new[] { "en-US", "pl-PL" })
                {
                    var dict = Dictionary($"Resources/Strings.{culture}.xaml");
                    Func<string, string, string> L = (key, fallback) => dict.Contains(key) ? dict[key] as string : fallback;
                    var seen = new HashSet<string>();
                    foreach (CaptureStatus status in Enum.GetValues(typeof(CaptureStatus)))
                    {
                        string key = CaptureVisibility.StatusKey(status) ?? "look_Recording_Status_Unknown";
                        Assert.IsTrue(dict.Contains(key), $"Strings.{culture}.xaml has no \"{key}\"");
                        string line = SettingsWindow.RecordingStatusLine(status, L);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(line), $"{culture}: {status} has a sentence");
                        Assert.IsTrue(seen.Add(line), $"{culture}: {status} has a sentence of its own");
                        Assert.IsFalse(line.Contains("OCR", StringComparison.OrdinalIgnoreCase), "no reader jargon in a player's line");
                    }
                    foreach (string key in new[] { "look_Recording_Title", "look_Recording_Show", "look_Recording_Mark", "look_Recording_Mark_Hint" })
                        Assert.IsTrue(dict.Contains(key) && !string.IsNullOrWhiteSpace(dict[key] as string), $"Strings.{culture}.xaml: {key}");
                }
            });
        }

        [TestMethod]
        public void TheShownSentence_SaysWhichObsSourceToPick_AndTheRefusedOneAdmitsIt()
        {
            StringAssert.Contains(CaptureVisibility.StatusFallback(CaptureStatus.Shown), "display capture");
            StringAssert.Contains(CaptureVisibility.StatusFallback(CaptureStatus.ShownWindowsRefused), "still shows");
            Assert.IsNull(CaptureVisibility.StatusKey(CaptureStatus.Unknown));
            Assert.IsNull(CaptureVisibility.StatusFallback(CaptureStatus.Unknown));
        }

        // ── The mask ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Mask_CoversThePanelInsideTheFrame_InFrameCoordinates()
        {
            var capture = new Rectangle(1000, 800, 600, 200);
            var panel = new Rectangle(1100, 760, 300, 90); // sticks out above the read area
            var fills = OverlayMask.Plan(capture, new[] { panel });
            CollectionAssert.AreEqual(new[] { new Rectangle(100, 0, 300, 50) }, fills);
        }

        [TestMethod]
        public void Mask_AlsoCoversWhereThePanelWasAtThePreviousRead()
        {
            var capture = new Rectangle(0, 0, 400, 200);
            var now = new[] { new Rectangle(50, 50, 100, 40) };
            var before = new[] { new Rectangle(60, 120, 100, 40) }; // the panel moved between two reads
            var fills = OverlayMask.Plan(capture, now, before);
            CollectionAssert.AreEquivalent(new[] { new Rectangle(50, 50, 100, 40), new Rectangle(60, 120, 100, 40) }, fills);
            Assert.AreEqual(1, OverlayMask.Plan(capture, now, now).Count, "an unmoved panel is filled once");
        }

        [TestMethod]
        public void AreaHistory_KeepsEveryPlaceSeenInOneTick_AndTheTickBefore()
        {
            var a = new Rectangle(10, 10, 100, 40);
            var b = new Rectangle(10, 60, 80, 40);
            var c = new Rectangle(200, 10, 100, 40);
            var history = new OverlayAreaHistory(500);
            history.NextTick();
            Assert.AreEqual(0, history.Observe(new[] { a }, 1000).Count, "nothing earlier at the first read");
            // A second read of the same tick, after the panel moved from A to B: A must still be covered.
            CollectionAssert.AreEqual(new[] { a }, history.Observe(new[] { b }, 1005).ToList());
            // The next tick sees both places of the tick before.
            history.NextTick();
            CollectionAssert.AreEquivalent(new[] { a, b }, history.Observe(new[] { c }, 1100).ToList());
            // And the tick after that has forgotten them.
            history.NextTick();
            CollectionAssert.AreEqual(new[] { c }, history.Observe(new[] { c }, 1200).ToList());
            history.NextTick();
            CollectionAssert.AreEqual(new[] { c }, history.Observe(Array.Empty<Rectangle>(), 1300).ToList(), "a panel that just vanished is covered once more");
            history.NextTick();
            Assert.AreEqual(0, history.Observe(Array.Empty<Rectangle>(), 1400).Count);
        }

        [TestMethod]
        public void AreaHistory_ALateReadOfTheSameTick_StillCoversTheTickBefore()
        {
            // The stamina strip copies the frame the tick already holds, however late it runs: that frame can still
            // show where the panel was a tick ago, so the tick's later reads mask it like its first.
            var a = new Rectangle(0, 0, 10, 10);
            var b = new Rectangle(20, 0, 10, 10);
            var history = new OverlayAreaHistory(500);
            history.NextTick();
            history.Observe(new[] { a }, 1000);
            history.NextTick();
            CollectionAssert.AreEqual(new[] { a }, history.Observe(new[] { b }, 1100).ToList(), "the dialogue read");
            CollectionAssert.AreEquivalent(new[] { a, b }, history.Observe(new[] { b }, 1120).ToList(), "the answer read");
            CollectionAssert.AreEquivalent(new[] { a, b }, history.Observe(new[] { b }, 1160).ToList(), "the stamina read, 60 ms into the tick");
            Assert.AreEqual(2, history.Observe(null, 1170).Count, "a read with nothing on screen still gets the earlier places");
        }

        [TestMethod]
        public void AreaHistory_AnOldTick_IsNotMadeFreshByANewOne()
        {
            // Translation paused for nine seconds (the game lost focus), then a tick saw the panel at B. Where it
            // was before the pause is in no frame any more: neither the tick's own reads nor a Ctrl+Q may mask it.
            var a = new Rectangle(0, 0, 10, 10);
            var b = new Rectangle(20, 0, 10, 10);
            var history = new OverlayAreaHistory(500);
            history.NextTick();
            history.Observe(new[] { a }, 1_000);
            history.NextTick();
            Assert.AreEqual(0, history.Observe(new[] { b }, 10_000).Count, "the tick's own read does not mask a place nine seconds old");
            CollectionAssert.AreEqual(new[] { b }, history.Recent(10_010).ToList(), "and seeing B did not make A fresh again");
            CollectionAssert.AreEqual(new[] { b }, history.WithRecent(new[] { b }, 10_010).ToList());
            CollectionAssert.AreEqual(new[] { b }, history.Observe(new[] { b }, 10_020).ToList(), "a later read of the same tick still has this tick's places");
        }

        [TestMethod]
        public void AreaHistory_AOneShotRead_GetsTheLastTicksOnlyWhileTheyAreFresh()
        {
            // Ctrl+Q reads outside the capture tick: where the panel was a moment ago can still be in its frame, but
            // places from a session that stopped long ago must not black out the game text it is asked to read.
            var a = new Rectangle(0, 0, 10, 10);
            var b = new Rectangle(20, 0, 10, 10);
            var history = new OverlayAreaHistory(500);
            Assert.AreEqual(0, history.Recent(5000).Count, "nothing was ever seen");
            history.NextTick();
            history.Observe(new[] { a }, 10_000);
            history.NextTick();
            history.Observe(new[] { b }, 10_100);
            CollectionAssert.AreEquivalent(new[] { a, b }, history.Recent(10_150).ToList(), "both ticks, 50 ms later");
            CollectionAssert.AreEquivalent(new[] { a, b }, history.Recent(10_500).ToList(), "the older tick is fresh up to its limit");
            CollectionAssert.AreEqual(new[] { b }, history.Recent(10_501).ToList(), "then only the newer one");
            Assert.AreEqual(0, history.Recent(10_601).Count, "stale: translation stopped, nothing shows there any more");
            Assert.AreEqual(0, history.Recent(9_000).Count, "a clock that went backwards is not trusted");
            CollectionAssert.AreEqual(new[] { b }, history.Observe(new[] { b }, 10_700).ToList(), "asking noted nothing: this tick's place is still here, the stale tick before is not");
        }

        [TestMethod]
        public void AOneOffRead_MasksWhereThePanelIsNow_AndWhereTheLastTicksSawIt()
        {
            // The panel moved from A to B just before the dialogue-area search captured: both of its questions answer
            // B, the frame still shows A. The fresh history supplies A.
            var a = new Rectangle(100, 500, 600, 80);
            var b = new Rectangle(100, 620, 400, 60);
            var history = new OverlayAreaHistory(500);
            history.NextTick();
            history.Observe(new[] { a }, 20_000);
            var areas = history.WithRecent(new[] { b }, 20_040);
            CollectionAssert.AreEquivalent(new[] { a, b }, areas.ToList());
            var fills = OverlayMask.Plan(new Rectangle(0, 0, 1920, 1080), areas);
            CollectionAssert.Contains(fills, a, "the place the frame still shows is blacked out");
            CollectionAssert.Contains(fills, b);
            // Long after translation stopped only the current place counts, and nothing is listed twice.
            CollectionAssert.AreEqual(new[] { b }, history.WithRecent(new[] { b }, 30_000).ToList());
            CollectionAssert.AreEqual(new[] { a }, history.WithRecent(new[] { a }, 20_040).ToList());
            Assert.AreEqual(0, new OverlayAreaHistory(500).WithRecent(null, 0).Count);
        }

        [TestMethod]
        public void Mask_LeavesAFrameWithNothingOfKaptionsAlone()
        {
            var capture = new Rectangle(0, 0, 400, 200);
            Assert.AreEqual(0, OverlayMask.Plan(capture, new[] { new Rectangle(500, 0, 100, 40) }).Count);
            Assert.AreEqual(0, OverlayMask.Plan(capture, Array.Empty<Rectangle>(), null).Count);
            Assert.AreEqual(0, OverlayMask.Plan(capture, null).Count);
            Assert.AreEqual(0, OverlayMask.Plan(capture, new[] { new Rectangle(10, 10, 0, 40) }).Count, "an empty area fills nothing");
        }

        [TestMethod]
        public void Mask_OnAMonitorLeftOfThePrimary_UsesNegativeScreenPixels()
        {
            var capture = new Rectangle(-1920, 700, 800, 200);
            var fills = OverlayMask.Plan(capture, new[] { new Rectangle(-1800, 750, 200, 60) });
            CollectionAssert.AreEqual(new[] { new Rectangle(120, 50, 200, 60) }, fills);
        }

        [TestMethod]
        public void Mask_BlacksOutExactlyThePanel_AndNothingBesideIt()
        {
            using var frame = new System.Drawing.Bitmap(200, 100, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(frame)) g.Clear(System.Drawing.Color.White);
            int filled = OverlayMask.Apply(frame, new Rectangle(300, 300, 200, 100), new[] { new Rectangle(340, 320, 60, 30) });
            Assert.AreEqual(1, filled);
            Assert.AreEqual(System.Drawing.Color.Black.ToArgb(), frame.GetPixel(40, 20).ToArgb(), "the panel's corner");
            Assert.AreEqual(System.Drawing.Color.Black.ToArgb(), frame.GetPixel(99, 49).ToArgb(), "the panel's far corner");
            Assert.AreEqual(System.Drawing.Color.White.ToArgb(), frame.GetPixel(39, 20).ToArgb(), "one pixel left of it");
            Assert.AreEqual(System.Drawing.Color.White.ToArgb(), frame.GetPixel(100, 50).ToArgb(), "one pixel past it");
            Assert.AreEqual(0, OverlayMask.Apply(null, new Rectangle(0, 0, 1, 1), new[] { new Rectangle(0, 0, 1, 1) }));
        }

        // ── The mark ────────────────────────────────────────────────────────────

        [TestMethod]
        [DataRow(10, 14.0)]
        [DataRow(22, 15.0)]
        [DataRow(36, 16.0)]
        public void Mark_Is14To16_AndGrowsWithTheSubtitle(int size, double side)
        {
            Assert.AreEqual(side, KaptionMarkLayout.Side(size));
        }

        [TestMethod]
        [DataRow(10)]
        [DataRow(22)]
        [DataRow(36)]
        public void MarkBox_SitsInTheRightPadding_InsideThePanel_NeverOverTheText(int size)
        {
            var style = new OverlayStyle { Size = size, Mark = true };
            var pad = style.Padding;
            foreach (var panel in new[] { new Rect(0, 0, 420, 52 * style.Scale + 20), new Rect(30, 10, 900, 160) })
            {
                var box = KaptionMarkLayout.Box(panel, size, pad, style.RenderSize);
                Assert.IsFalse(box.IsEmpty, $"size {size}: a panel this large has room");
                Assert.AreEqual(KaptionMarkLayout.Side(size), box.Width);
                Assert.AreEqual(box.Width, box.Height);
                var text = new Rect(panel.Left + pad.Left, panel.Top + pad.Top, panel.Width - pad.Left - pad.Right, panel.Height - pad.Top - pad.Bottom);
                Assert.IsFalse(box.IntersectsWith(text) && Rect.Intersect(box, text).Width > 0.01, $"size {size}: the mark is beside the text, not over it");
                Assert.IsTrue(panel.Contains(box), $"size {size}: the mark is inside the panel");
                Assert.IsTrue(box.Left >= text.Right, $"size {size}: the mark is at the end of the band");
            }
        }

        [TestMethod]
        public void MarkBox_IsEmpty_WhenThePanelHasNoRoom()
        {
            var style = new OverlayStyle { Mark = true };
            Assert.IsTrue(KaptionMarkLayout.Box(new Rect(0, 0, 20, 8), style.Size, style.Padding, style.RenderSize).IsEmpty);
            Assert.IsTrue(KaptionMarkLayout.Box(Rect.Empty, style.Size, style.Padding, style.RenderSize).IsEmpty);
            Assert.IsTrue(KaptionMarkLayout.Box(new Rect(0, 0, 400, 60), style.Size, new Thickness(4), style.RenderSize).IsEmpty, "no column for it");
        }

        [TestMethod]
        public void TheMark_IsOffByDefault_AndThenThePanelIsExactlyAsBefore()
        {
            Assert.IsFalse(OverlayStyle.Default.Mark);
            foreach (int size in new[] { 10, 22, 36 })
            {
                var plain = new OverlayStyle { Size = size };
                Assert.AreEqual(new Thickness(18 * plain.Scale, 10 * plain.Scale, 18 * plain.Scale, 12 * plain.Scale), plain.Padding);
                var marked = plain with { Mark = true };
                Assert.AreEqual(marked.Padding.Left, marked.Padding.Right, "the text stays centred");
                Assert.IsTrue(marked.Padding.Right >= KaptionMarkLayout.Column(size), "the mark has its own column");
                Assert.AreEqual(plain.Padding.Top, marked.Padding.Top);
                Assert.AreEqual(plain.Padding.Bottom, marked.Padding.Bottom);
            }
        }

        [TestMethod]
        public void TheMark_IsSavedWithTheLook_AndRestoreDefaultsClearsIt()
        {
            WithKeysCleared(LookKeys, () =>
            {
                Assert.IsFalse(OverlayStyle.FromConfig().Mark, "an install without the key has no mark");
                var saved = OverlayStyle.FromConfig();
                (saved with { Mark = true }).Save(saved);
                Assert.IsTrue(OverlayStyle.FromConfig().Mark);
                OverlayStyle.ResetConfig();
                Assert.IsFalse(AppConfig.Has(OverlayStyle.MarkKey));
            });
        }

        [TestMethod]
        public void ThePanel_DrawsTheMark_OnlyWhenAsked_AndOnlyInItsBox()
        {
            RunSta(() =>
            {
                Assert.IsNotNull(OverlayTokens.Mark, "the brand mark loads from Themes/Tokens.xaml");
                var shots = new List<BitmapSource>();
                foreach (int size in new[] { 10, 22, 36 })
                {
                    BitmapSource plain = null, marked = null;
                    Rect box = Rect.Empty;
                    foreach (bool mark in new[] { false, true })
                    {
                        var style = new OverlayStyle { Size = size, Mark = mark };
                        var card = OverlayRenderTests.Card(style, "Paimon", "Ej, poczekaj! Paimon też chce zobaczyć, co jest za tą bramą.");
                        var host = OverlayRenderTests.Scene(null, card, 960, 220);
                        Layout(host, 960, 220);
                        var shot = Snapshot(host);
                        if (!mark) { plain = shot; continue; }
                        marked = shot;
                        // The panel draws the mark in its own coordinates; the shot is in the host's.
                        box = KaptionMarkLayout.Box(new Rect(card.RenderSize), size, style.Padding, card.SubtitleText.FontSize);
                        var origin = card.TranslatePoint(new Point(0, 0), host);
                        box.Offset(origin.X, origin.Y);
                        shots.Add(shot);
                    }
                    Assert.IsTrue(BluePixels(marked, box) > box.Width * box.Height * 0.2, $"size {size}: the K mark is drawn in its box");
                    Assert.AreEqual(0, BluePixels(plain, new Rect(0, 0, plain.PixelWidth, plain.PixelHeight)), $"size {size}: no mark unless asked");
                    var outside = new Rect(0, 0, marked.PixelWidth, marked.PixelHeight);
                    Assert.AreEqual(BluePixels(marked, box), BluePixels(marked, outside), $"size {size}: the mark stays inside its box");
                }
                SaveReview(SideBySide(shots), "overlay/kaption-mark-sizes.png");
            });
        }

        [TestMethod]
        public void TheMark_Waits_WhileThePanelIsStillGrowingTowardsItsPlace()
        {
            RunSta(() =>
            {
                var style = new OverlayStyle { Mark = true };
                var card = OverlayRenderTests.Card(style, "Paimon", "Ej, poczekaj! Paimon też chce zobaczyć, co jest za tą bramą.");
                var host = OverlayRenderTests.Scene(null, card, 960, 220);
                Layout(host, 960, 220);
                var whole = new Rect(0, 0, 960, 220);
                Assert.IsTrue(BluePixels(Snapshot(host), whole) > 0, "at rest the mark is drawn");
                // A short line became this long one: the text is laid out wide already, the panel is drawn half-way.
                card.PanelWidth = card.ActualWidth / 2;
                Layout(host, 960, 220);
                Assert.AreEqual(0, BluePixels(Snapshot(host), whole), "the growing panel does not reach the mark's place yet: no mark over the text");
                // Shrinking: the panel is drawn wider than its new layout and contains the mark's place.
                card.PanelWidth = card.ActualWidth + 80;
                Layout(host, 960, 220);
                Assert.IsTrue(BluePixels(Snapshot(host), whole) > 0, "a panel that still covers the mark's place draws it");
            });
        }

        /// <summary>Pixels of the brand blue (the mark's tile) inside <paramref name="box"/>.</summary>
        private static int BluePixels(BitmapSource bitmap, Rect box)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bitmap.CopyPixels(pixels, stride, 0);
            int x0 = Math.Max(0, (int)Math.Floor(box.Left)), x1 = Math.Min(w, (int)Math.Ceiling(box.Right));
            int y0 = Math.Max(0, (int)Math.Floor(box.Top)), y1 = Math.Min(h, (int)Math.Ceiling(box.Bottom));
            int count = 0;
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * stride + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (b > 150 && b - r > 70 && b - g > 40) count++;
            }
            return count;
        }
    }
}
