using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The 2.6 subtitle panel (SubtitleCard, the control the overlay window uses) drawn over game frames: every look, the
    /// two-line balance, the name tag, the bright-scene darkening, the faces. Review PNGs go to %TEMP%\kaption-w26\renders.
    /// </summary>
    [TestClass]
    public class OverlayRenderTests
    {
        internal const string TiantianPl =
            "Zlecenia? ...Och, wiem, masz na myśli bogatych ludzi, którzy chcą przeżyć przygodę, ale martwią się o bezpieczeństwo, więc wynajmują nas, żeby zabrać ich na survivalowe wyprawy. Tak?";
        internal const string PaimonPl = "Ej, poczekaj! Paimon też chce zobaczyć, co jest za tą bramą.";

        private static string Repo => RepoRoot();

        /// <summary>The bright plaza of a real Genshin frame (landing/public/en-pl-npc-dialog.jpg, above its baked-in 2.4 panel).</summary>
        internal static BitmapSource Plaza()
        {
            string path = Path.Combine(Repo, "landing", "public", "en-pl-npc-dialog.jpg");
            if (!File.Exists(path)) return null;
            var shot = new BitmapImage(new Uri(path));
            return new CroppedBitmap(shot, new Int32Rect(0, 250, shot.PixelWidth, Math.Min(520, shot.PixelHeight - 250)));
        }

        internal static BitmapSource Ambience(string game)
        {
            string path = Path.Combine(Repo, "GI-Subtitles", "Assets", "Ambience", AmbienceArt.ForGame(game) + ".jpg");
            return File.Exists(path) ? (BitmapSource)AmbienceArt.LoadFile(path) : null;
        }

        /// <summary>A frame with the panel where the overlay puts it: centred, above the bottom edge.</summary>
        internal static Grid Scene(BitmapSource backdrop, SubtitleCard card, double width = 1280, double height = 360, double luminance = 0)
        {
            var host = new Grid { Width = width, Height = height, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x1C)) };
            if (backdrop != null)
                host.Children.Add(new Image { Source = backdrop, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center });
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Bottom;
            card.Margin = new Thickness(0, 0, 0, 36);
            card.SmoothResize = false;
            card.BackdropLuminance = luminance;
            card.SubtitleText.MaxTextWidth = Math.Min(900, width - 120);
            card.HeaderText.MaxTextWidth = card.SubtitleText.MaxTextWidth;
            host.Children.Add(card);
            return host;
        }

        internal static SubtitleCard Card(OverlayStyle style, string speaker, string line, Color? hue = null)
        {
            var card = new SubtitleCard();
            card.ApplyStyle(style);
            card.SetSpeakerHue(hue);
            card.SubtitleText.Text = line;
            card.HeaderText.Text = speaker ?? string.Empty;
            card.HeaderText.Visibility = string.IsNullOrEmpty(speaker) ? Visibility.Collapsed : Visibility.Visible;
            return card;
        }

        [TestMethod]
        public void RendersEveryLook_OverABrightAndADarkScene()
        {
            var written = new List<string>();
            RunSta(() =>
            {
                var plaza = Plaza();
                var night = Ambience("Genshin");
                foreach (OverlayPreset preset in Enum.GetValues(typeof(OverlayPreset)))
                {
                    var style = OverlayStyle.Default.WithPreset(preset);
                    foreach (var (backdrop, slug, lum) in new[] { (plaza, "jasna", 0.78), (night, "ciemna", 0.12) })
                    {
                        var host = Scene(backdrop, Card(style, "Tiantian", TiantianPl), luminance: lum);
                        Layout(host, 1280, 360);
                        var card = host.Children.OfType<SubtitleCard>().Single();
                        Assert.IsTrue(card.SubtitleText.LineCount <= 3, $"{preset}: {card.SubtitleText.LineCount} lines");
                        written.Add(SaveReview(Snapshot(host), $"overlay/look-{preset.ToString().ToLowerInvariant()}-{slug}.png"));
                    }
                }
            });
            foreach (string p in written) Console.WriteLine(p);
        }

        [TestMethod]
        public void BalancesTwoLines_WhereTheOldPanelLeftAStub()
        {
            RunSta(() =>
            {
                var plaza = Plaza();
                var balanced = Card(OverlayStyle.Default, "Tiantian", TiantianPl);
                var host = Scene(plaza, balanced);
                Layout(host, 1280, 360);
                var widths = LineWidths(balanced.SubtitleText);
                Assert.IsTrue(widths.Count >= 2, "the Tiantian line wraps in a 900 px panel");
                Assert.AreEqual(balanced.SubtitleText.LineCount, widths.Count);
                Assert.IsTrue(widths.Min() / widths.Max() > 0.8, $"balanced lines: {string.Join(" / ", widths.Select(w => w.ToString("0")))}");
                SaveReview(Snapshot(host), "overlay/balance-tiantian.png");

                // The same text unbalanced, for the review: a full first line and a short last one.
                var stub = Card(OverlayStyle.Default, "Tiantian", TiantianPl);
                stub.SubtitleText.BalanceLines = false;
                var hostStub = Scene(plaza, stub);
                Layout(hostStub, 1280, 360);
                var stubWidths = LineWidths(stub.SubtitleText);
                Assert.IsTrue(stubWidths.Min() / stubWidths.Max() < widths.Min() / widths.Max(), "balancing evens the lines out");
                SaveReview(Snapshot(hostStub), "overlay/balance-tiantian-before.png");
            });
        }

        [TestMethod]
        public void NameTag_IsNeutral_UnlessTheElementIsKnown()
        {
            RunSta(() =>
            {
                var night = Ambience("Genshin");
                var shots = new List<BitmapSource>();
                foreach (var (speaker, hue, slug) in new (string, Color?, string)[]
                {
                    ("Paimon", null, "neutral"),
                    ("Chiori", ElementPalette.For("genshin", "Geo"), "geo"),
                    ("Furina", ElementPalette.For("genshin", "Hydro"), "hydro"),
                })
                {
                    var host = Scene(night, Card(OverlayStyle.Default, speaker, PaimonPl, hue), 960, 220);
                    Layout(host, 960, 220);
                    var shot = Snapshot(host);
                    shots.Add(shot);
                    SaveReview(shot, $"overlay/name-tag-{slug}.png");
                }
                SaveReview(SideBySide(shots), "overlay/name-tags.png");
            });
        }

        [TestMethod]
        public void PanelDarkens_OverABrightScene_AndKeepsTheSavedTintOnADarkOne()
        {
            RunSta(() =>
            {
                var plaza = Plaza();
                var shots = new List<BitmapSource>();
                foreach (double lum in new[] { 0.0, 0.9 })
                {
                    var host = Scene(plaza, Card(OverlayStyle.Default, "Tiantian", PaimonPl), 960, 260, lum);
                    Layout(host, 960, 260);
                    shots.Add(Snapshot(host));
                }
                SaveReview(SideBySide(shots), "overlay/auto-darken-off-on.png");
                Assert.AreEqual((byte)176, SubtitleCard.DarkenedAlpha(176, 0.2));
                Assert.AreEqual((byte)232, SubtitleCard.DarkenedAlpha(176, 0.95));
            });
        }

        [TestMethod]
        public void Faces_SourceSans3_InterTight_AndThe24Look()
        {
            RunSta(() =>
            {
                var plaza = Plaza();
                var shots = new List<BitmapSource>();
                const string diacritics = "Zażółć gęślą jaźń. Ćma, łódź, źrebię, żółw: 1234567890 „cytat” … ąęłńóśźż";
                foreach (string face in new[] { OverlayFonts.SourceSans, OverlayFonts.InterTight, "Segoe UI" })
                {
                    var host = Scene(plaza, Card(OverlayStyle.Default with { FontFamily = face }, face, diacritics), 1100, 220, 0.78);
                    Layout(host, 1100, 220);
                    shots.Add(Snapshot(host));
                }
                SaveReview(shots[0], "overlay/face-source-sans-3.png");
                SaveReview(shots[1], "overlay/face-inter-tight.png");
                SaveReview(shots[2], "overlay/face-segoe-ui.png");
            });
        }

        [TestMethod]
        public void Sizes_FromSmallToLarge()
        {
            RunSta(() =>
            {
                var night = Ambience("StarRail");
                foreach (int size in new[] { 16, 22, 28, 36 })
                {
                    var host = Scene(night, Card(OverlayStyle.Default with { Size = size }, "March 7th", PaimonPl), 1280, 300);
                    Layout(host, 1280, 300);
                    SaveReview(Snapshot(host), $"overlay/size-{size}.png");
                }
            });
        }

        internal static List<double> LineWidths(OutlinedTextBlock block)
        {
            var layout = OverlayTextLayout.Build(block.Text,
                new Typeface(OverlayFonts.Resolve(block.FontFamily), block.FontStyle, block.FontWeight, block.FontStretch),
                block.FontSize, block.MaxTextWidth - block.StrokeThickness * 2, block.LetterSpacing, block.BalanceLines,
                TextAlignment.Center, 1.0);
            return layout.LineWidths.ToList();
        }
    }
}
