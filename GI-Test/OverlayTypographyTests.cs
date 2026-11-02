using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GI_Subtitles.Services.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
// GI_Test has a Config of its own; the overlay reads the app's.
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// The 2.6 overlay: bundled faces, the text layout (spacing, balanced lines), the style read from Config (a saved size,
    /// position and panel keep their meaning), where the panel sits, and the panel control itself.
    /// </summary>
    [TestClass]
    public class OverlayTypographyTests
    {
        private static readonly string[] StyleKeys =
        {
            OverlayStyle.SizeKey, OverlayStyle.OpacityKey, OverlayStyle.PadKey, OverlayStyle.LegacyFontKey, OverlayStyle.FontKey,
            OverlayStyle.OutlineKey, OverlayStyle.LetterSpacingKey, OverlayStyle.AutoDarkenKey,
        };

        /// <summary>Runs <paramref name="test"/> with the style keys cleared, then puts back what was saved.</summary>
        private static void WithCleanStyleKeys(Action test)
        {
            var saved = StyleKeys.Where(AppConfig.Has).ToDictionary(k => k, k => AppConfig.Get<System.Text.Json.Nodes.JsonNode>(k, null)?.ToJsonString());
            foreach (string k in StyleKeys) AppConfig.Remove(k);
            try { test(); }
            finally
            {
                foreach (string k in StyleKeys) AppConfig.Remove(k);
                foreach (var kv in saved)
                    if (kv.Value != null) AppConfig.Set(kv.Key, System.Text.Json.Nodes.JsonNode.Parse(kv.Value));
            }
        }

        // ── Faces ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void BundledFaces_LoadFromTheAssembly_WithPolishDiacritics()
        {
            RunSta(() =>
            {
                foreach (string face in OverlayFonts.Bundled)
                foreach (var weight in new[] { FontWeights.SemiBold, FontWeights.Bold })
                {
                    var typeface = new Typeface(OverlayFonts.Resolve(face), FontStyles.Normal, weight, FontStretches.Normal);
                    Assert.IsTrue(typeface.TryGetGlyphTypeface(out var glyphs), $"{face} {weight} resolves to a font file");
                    string family = glyphs.FamilyNames.Values.FirstOrDefault() ?? "";
                    StringAssert.StartsWith(family, face, $"{face} {weight} is the bundled face, not a fallback");
                    Assert.AreEqual(weight.ToOpenTypeWeight(), glyphs.Weight.ToOpenTypeWeight(), $"{face} carries {weight}");
                    foreach (char c in "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ„”…–")
                        Assert.IsTrue(glyphs.CharacterToGlyphMap.ContainsKey(c), $"{face} {weight} has {c}");
                }
            });
        }

        [TestMethod]
        public void BareFamilyNames_ResolveToTheBundledFaces_SystemFontsPassThrough()
        {
            RunSta(() =>
            {
                var resolved = OverlayFonts.Resolve(new FontFamily(OverlayFonts.SourceSans));
                Assert.IsNotNull(resolved.BaseUri, "the promo renderer's new FontFamily(\"Source Sans 3\") reaches the embedded file");
                StringAssert.Contains(resolved.Source, "#Source Sans 3");
                StringAssert.Contains(resolved.Source, "#Inter Tight", "Inter Tight is the fallback face");
                Assert.AreEqual("Consolas", OverlayFonts.Resolve("Consolas").Source);
                Assert.AreEqual(1.0, OverlayFonts.FaceScale("Segoe UI"));
                Assert.IsTrue(OverlayFonts.FaceScale(OverlayFonts.SourceSans) > 1.0, "Source Sans 3 is drawn a little larger so 22 still looks like 22");
            });
        }

        // ── Text layout ─────────────────────────────────────────────────────────

        private static OverlayTextBlockLayout Lay(string text, double width, double tracking = 0, bool balance = true, string face = OverlayFonts.SourceSans) =>
            OverlayTextLayout.Build(text, new Typeface(OverlayFonts.Resolve(face), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                22.88, width, tracking, balance, TextAlignment.Center, 1.0);

        [TestMethod]
        public void BalancedLines_AreNearlyEqual_AndTheLineCountDoesNotGrow()
        {
            RunSta(() =>
            {
                var natural = Lay(OverlayRenderTests.TiantianPl, 860, balance: false);
                var balanced = Lay(OverlayRenderTests.TiantianPl, 860);
                Assert.AreEqual(natural.Lines, balanced.Lines, "balancing never adds a line");
                Assert.IsTrue(balanced.LineWidths.Min() / balanced.LineWidths.Max() > 0.8,
                    "balanced: " + string.Join(" / ", balanced.LineWidths.Select(w => w.ToString("0"))));
                Assert.IsTrue(balanced.Width <= 860, "lines stay inside the wrap width");
            });
        }

        [TestMethod]
        public void LetterSpacing_WidensTheLine_ByTheSpacingTimesTheGaps()
        {
            RunSta(() =>
            {
                const string text = "Ej, poczekaj!";
                var plain = Lay(text, double.PositiveInfinity);
                var spaced = Lay(text, double.PositiveInfinity, tracking: 0.05);
                double expected = 0.05 * 22.88 * (text.Length - 1);
                Assert.AreEqual(plain.Width + expected, spaced.Width, 1.0, "one spacing between each pair of letters");
                Assert.AreEqual(1, spaced.Lines);
            });
        }

        [TestMethod]
        public void LetterSpacing_StillFitsTheWrapWidth()
        {
            RunSta(() =>
            {
                var spaced = Lay(OverlayRenderTests.TiantianPl, 860, tracking: 0.03);
                Assert.IsTrue(spaced.LineWidths.All(w => w <= 860.5), string.Join(" / ", spaced.LineWidths.Select(w => w.ToString("0"))));
            });
        }

        [TestMethod]
        public void TextOutsideTheFaces_FallsBackInsteadOfDisappearing()
        {
            RunSta(() =>
            {
                var cjk = Lay("你好，派蒙", double.PositiveInfinity);
                var cyrillic = Lay("Привет, Паймон", double.PositiveInfinity, face: OverlayFonts.InterTight);
                Assert.IsFalse(cjk.Geometry.IsEmpty(), "Chinese draws through Windows' fallback fonts");
                Assert.IsTrue(cjk.Width > 40);
                Assert.IsFalse(cyrillic.Geometry.IsEmpty(), "Cyrillic draws though Inter Tight's cut has no Cyrillic");
            });
        }

        [TestMethod]
        public void EmptyText_HasALineHeightAndNoGeometry()
        {
            RunSta(() =>
            {
                var empty = Lay(string.Empty, 500);
                Assert.AreEqual(0, empty.Lines);
                Assert.IsTrue(empty.Height > 20);
                Assert.IsTrue(empty.Geometry.IsEmpty());
            });
        }

        [TestMethod]
        public void OldMeasureText_StillMeasuresWithTheStrokeOnBothSides()
        {
            RunSta(() =>
            {
                var size = OutlinedTextBlock.MeasureText("Paimon", 22, new FontFamily("Segoe UI"), FontWeights.Bold, FontStyles.Normal,
                    double.PositiveInfinity, 2, 1.0);
                var ft = new FormattedText("Paimon", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 22, Brushes.White, 1.0);
                Assert.AreEqual(ft.Width + 4, size.Width, 0.5);
                Assert.AreEqual(ft.Height + 4, size.Height, 0.5);
            });
        }

        // ── Style from Config: the update keeps what people saved ─────────────

        [TestMethod]
        public void NothingSaved_ReadsTheDefaults()
        {
            WithCleanStyleKeys(() =>
            {
                var s = OverlayStyle.FromConfig();
                Assert.AreEqual(22, s.Size);
                Assert.AreEqual(OverlayFonts.SourceSans, s.FontFamily);
                Assert.AreEqual(176, s.BackgroundOpacity);
                Assert.IsNull(s.PadVertical, "no saved position: the game's own default");
                Assert.AreEqual(OverlayPreset.Classic, s.MatchingPreset());
            });
        }

        [TestMethod]
        public void SavedSizePositionAndPanel_ReadUnchanged()
        {
            WithCleanStyleKeys(() =>
            {
                AppConfig.Set(OverlayStyle.SizeKey, 28);
                AppConfig.Set(OverlayStyle.OpacityKey, 210);
                AppConfig.Set(OverlayStyle.PadKey, new[] { -230, 40 });
                var s = OverlayStyle.FromConfig();
                Assert.AreEqual(28, s.Size);
                Assert.AreEqual(210, s.BackgroundOpacity);
                Assert.AreEqual(-230, s.PadVertical);
                Assert.AreEqual(40, s.PadHorizontal);
                Assert.IsNull(s.MatchingPreset(), "a hand-set size is no preset");
            });
        }

        [TestMethod]
        public void TheOldDefaultFont_ReadsAsTheNewFace_AChosenOneStays()
        {
            WithCleanStyleKeys(() =>
            {
                AppConfig.Set(OverlayStyle.LegacyFontKey, "Segoe UI");
                Assert.AreEqual(OverlayFonts.SourceSans, OverlayStyle.FromConfig().FontFamily,
                    "every 2.5 launch wrote Segoe UI whether chosen or not, so it means the default");
                AppConfig.Set(OverlayStyle.LegacyFontKey, "Consolas");
                Assert.AreEqual("Consolas", OverlayStyle.FromConfig().FontFamily, "any other saved face was a choice");
                AppConfig.Set(OverlayStyle.FontKey, "Segoe UI");
                Assert.AreEqual("Segoe UI", OverlayStyle.FromConfig().FontFamily, "a 2.6 choice of Segoe UI is kept");
            });
        }

        [TestMethod]
        public void Presets_WriteTheirLook_AndKinoMovesTheSubtitleLowAndCentred()
        {
            WithCleanStyleKeys(() =>
            {
                var start = OverlayStyle.FromConfig() with { PadVertical = -175, PadHorizontal = 60 };
                var contrast = start.WithPreset(OverlayPreset.Contrast);
                Assert.AreEqual(OverlayPreset.Contrast, contrast.MatchingPreset());
                Assert.AreEqual(-175, contrast.PadVertical, "Kontrast keeps the position");
                Assert.AreEqual(60, contrast.PadHorizontal);

                double reference = 90;
                var cinema = start.WithPreset(OverlayPreset.Cinema, reference);
                Assert.AreEqual(OverlayPreset.Cinema, cinema.MatchingPreset());
                Assert.AreEqual(26, cinema.Size);
                Assert.AreEqual(0, cinema.PadHorizontal, "Kino is centred");
                Assert.AreEqual(OverlayStyle.SnugPad(reference), cinema.PadVertical);
                Assert.IsTrue(cinema.PadVertical > -175, "Kino sits lower than the default");

                cinema.Save(OverlayStyle.FromConfig());
                var read = OverlayStyle.FromConfig();
                Assert.AreEqual(OverlayPreset.Cinema, read.MatchingPreset(), "saved and read back as Kino");
                Assert.AreEqual(cinema.PadVertical, read.PadVertical);

                OverlayStyle.ResetConfig();
                Assert.AreEqual(OverlayPreset.Classic, OverlayStyle.FromConfig().MatchingPreset(), "Przywróć domyślne");
                Assert.IsFalse(AppConfig.Has(OverlayStyle.PadKey), "and the game's own position");
            });
        }

        [TestMethod]
        public void Kino_PutsThePanelJustAboveTheDialogue()
        {
            const double regionTop = 922, regionHeight = 150;
            double referenceWindow = 96;
            int pad = OverlayStyle.SnugPad(referenceWindow);
            double windowTop = OverlayPlacement.WindowTop(regionTop, regionHeight, pad, referenceWindow, referenceWindow);
            double panelBottom = windowTop + referenceWindow - OverlayStyle.WindowMargin;
            Assert.AreEqual(regionTop - OverlayStyle.CinemaGap, panelBottom, 0.5, "the panel ends a small gap above the game's dialogue");
            Assert.IsTrue(windowTop + referenceWindow <= regionTop, "and the overlay window never reaches into the dialogue area");
        }

        [TestMethod]
        public void DerivedSizes_ScaleFrom22()
        {
            var s22 = OverlayStyle.Default;
            var s33 = OverlayStyle.Default with { Size = 33 };
            Assert.AreEqual(1.0, s22.StrokeThickness, "a 1 px outline at 22");
            Assert.AreEqual(1.5, s33.StrokeThickness, 0.01);
            Assert.AreEqual(2.0, s22.ShadowOffset, "a 2 px drop at 22");
            Assert.AreEqual(FontWeights.Bold, (OverlayStyle.Default with { Size = 16 }).Weight, "small text a step heavier");
            Assert.IsTrue((OverlayStyle.Default with { Size = 16 }).Tracking > 0, "small text a little looser");
            Assert.IsTrue((OverlayStyle.Default with { Size = 34 }).Tracking < 0, "large text a little tighter");
        }

        // ── Where the panel sits ────────────────────────────────────────────────

        [TestMethod]
        public void AOneLineSubtitleWithAName_LandsWhereTheSavedOffsetAlwaysPutIt()
        {
            const double regionTop = 900, regionHeight = 140, pad = -175, reference = 92;
            double top = OverlayPlacement.WindowTop(regionTop, regionHeight, pad, reference, reference);
            Assert.AreEqual(regionTop + pad, top, "the reference panel's top is the pre-2.6 top");
        }

        [TestMethod]
        public void AboveTheDialogue_ALongerLineGrowsUpward_BelowItDownward()
        {
            const double regionTop = 900, regionHeight = 140, reference = 92;
            Assert.IsTrue(OverlayPlacement.GrowsUpward(regionTop, regionHeight, -175, reference));
            double tall = OverlayPlacement.WindowTop(regionTop, regionHeight, -175, reference, reference + 30);
            Assert.AreEqual(regionTop - 175 - 30, tall, "the bottom edge stays put");

            Assert.IsFalse(OverlayPlacement.GrowsUpward(regionTop, regionHeight, 160, reference), "placed under the dialogue");
            Assert.AreEqual(regionTop + 160, OverlayPlacement.WindowTop(regionTop, regionHeight, 160, reference, reference + 30));
        }

        [TestMethod]
        public void ADroppedPanel_SavesTheOffsetThatPutsItBackThere()
        {
            const double regionTop = 900, regionHeight = 140, reference = 92;
            foreach (double pad in new[] { -175.0, -320, 160 })
            foreach (double height in new[] { reference, reference + 41 })
            {
                double top = OverlayPlacement.WindowTop(regionTop, regionHeight, pad, reference, height);
                Assert.AreEqual((int)pad, OverlayPlacement.PadFor(top, height, regionTop, regionHeight, reference), $"pad {pad}, height {height}");
            }
        }

        // ── The panel control ───────────────────────────────────────────────────

        [TestMethod]
        public void PanelParts_KeepTheirNames_ForCodeThatFindsThemByName()
        {
            RunSta(() =>
            {
                var root = new System.Windows.Controls.Grid();
                var card = new SubtitleCard { Name = "SubtitleBackground" };
                root.Children.Add(card);
                foreach (string name in new[] { "HeaderText", "SubtitleText", "AnswerText", "AnswerSeparator" })
                    Assert.IsNotNull(LogicalTreeHelper.FindLogicalNode(root, name), $"the promo renderer finds {name}");
                Assert.IsInstanceOfType(LogicalTreeHelper.FindLogicalNode(root, "SubtitleText"), typeof(OutlinedTextBlock));
            });
        }

        [TestMethod]
        public void NameTag_ShowsOnlyWithAVisibleName()
        {
            RunSta(() =>
            {
                var card = new SubtitleCard();
                Assert.AreEqual(Visibility.Collapsed, card.HeaderPill.Visibility);
                card.HeaderText.Text = "Paimon";
                card.HeaderText.Visibility = Visibility.Visible;
                Assert.AreEqual(Visibility.Visible, card.HeaderPill.Visibility);
                card.HeaderText.Text = string.Empty;
                Assert.AreEqual(Visibility.Collapsed, card.HeaderPill.Visibility, "the hide-subtitles toggle shows an empty name: no blob");
                card.HeaderText.Text = "Paimon";
                card.HeaderText.Visibility = Visibility.Collapsed;
                Assert.AreEqual(Visibility.Collapsed, card.HeaderPill.Visibility);
            });
        }

        [TestMethod]
        public void ReferenceHeight_IsTheMeasuredPanelForOneLineWithAName()
        {
            RunSta(() =>
            {
                foreach (int size in new[] { 16, 22, 30 })
                {
                    var style = OverlayStyle.Default with { Size = size };
                    var card = OverlayRenderTests.Card(style, "Paimon", "Ej, poczekaj!");
                    card.Measure(new Size(1200, double.PositiveInfinity));
                    Assert.AreEqual(card.DesiredSize.Height, SubtitleCard.ReferenceHeight(style), 2.0, $"size {size}");
                }
            });
        }

        [TestMethod]
        public void PanelDarkening_RisesSmoothly_AndNeverLightens()
        {
            byte last = 0;
            for (double l = 0; l <= 1.0001; l += 0.05)
            {
                byte a = SubtitleCard.DarkenedAlpha(176, l);
                Assert.IsTrue(a >= last && a >= 176 && a <= 232, $"{l:0.00} → {a}");
                last = a;
            }
            Assert.AreEqual((byte)240, SubtitleCard.DarkenedAlpha(240, 1.0), "a darker saved panel stays as it is");
        }

        [TestMethod]
        public void BackdropBrightness_FromPixels()
        {
            Assert.AreEqual(0.0, BackdropSampler.Luminance(Enumerable.Repeat((byte)0, 4 * 64).ToArray()), 0.001);
            Assert.AreEqual(1.0, BackdropSampler.Luminance(Enumerable.Repeat((byte)255, 4 * 64).ToArray()), 0.001);
            // A bright patch behind a few letters counts for more than its share of the area.
            var mostlyDark = new byte[4 * 100];
            for (int i = 0; i < 12; i++) mostlyDark[i * 4] = mostlyDark[i * 4 + 1] = mostlyDark[i * 4 + 2] = 255;
            Assert.IsTrue(BackdropSampler.Luminance(mostlyDark) > 0.3);
        }

        // ── Auto-shrink and the two-line preference ─────────────────────────────

        private static SubtitleLayoutParams Params(Func<double, double, SubtitleContentMeasure> measure, int maxLines = 2, bool autoShrink = true) => new()
        {
            Text = "x", FontSize = 22, CaptureRegion = new[] { 160, 955, 1650, 255 }, Scale = 1, PadVertical = -175,
            ScreenBounds = new Rect(0, 0, 2560, 1440), MaxHeight = 0, MaxWidth = 900, AutoShrinkText = autoShrink, MinFontSize = 12,
            MeasureContent = measure, PreferredMaxLines = maxLines,
        };

        [TestMethod]
        public void ALongLine_ShrinksALittle_ToFitTwoLines()
        {
            // Three lines down to 20, two from 19.
            var r = new DefaultSubtitleLayoutEngine().CalculateLayout(Params((s, w) => new SubtitleContentMeasure(new Size(800, s * 5), s >= 20 ? 3 : 2)));
            Assert.AreEqual(19, r.EffectiveFontSize);
        }

        [TestMethod]
        public void AVeryLongLine_KeepsItsSize_RatherThanShrinkingPastAFifth()
        {
            var r = new DefaultSubtitleLayoutEngine().CalculateLayout(Params((s, w) => new SubtitleContentMeasure(new Size(800, s * 5), s >= 15 ? 3 : 2)));
            Assert.AreEqual(22, r.EffectiveFontSize, "three lines at 22 read better than two at 14");
        }

        [TestMethod]
        public void AutoShrinkOff_KeepsTheSizeAndTheLines()
        {
            var r = new DefaultSubtitleLayoutEngine().CalculateLayout(Params((s, w) => new SubtitleContentMeasure(new Size(800, s * 5), 3), autoShrink: false));
            Assert.AreEqual(22, r.EffectiveFontSize);
        }
    }
}
