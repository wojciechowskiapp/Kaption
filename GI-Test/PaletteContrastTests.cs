using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GI_Subtitles.Themes;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// The two semantic palettes: same keys, and WCAG AA (4.5:1) for every text/surface pair the shell draws,
    /// translucent tints composited over the surface they sit on. Plus ThemeService resolution and palette swapping.
    /// </summary>
    [TestClass]
    public class PaletteContrastTests
    {
        private static readonly string[] Surfaces = { "BgBrush", "SurfaceBrush", "Surface2Brush" };
        private static readonly string[] BodyText = { "TextBrush", "TextSecondaryBrush", "TextTertiaryBrush", "AccentTextBrush" };
        private static readonly string[] StatusText = { "SuccessBrush", "WarnBrush", "ErrorBrush", "InfoBrush" };

        [TestMethod]
        public void BothPalettesDefineTheSameKeys()
        {
            RunSta(() =>
            {
                var dark = Keys(Dictionary("Themes/Palette.Dark.xaml"));
                var light = Keys(Dictionary("Themes/Palette.Light.xaml"));
                CollectionAssert.AreEquivalent(light, dark,
                    "Missing in dark: " + string.Join(", ", light.Except(dark)) + "; missing in light: " + string.Join(", ", dark.Except(light)));
            });
        }

        [TestMethod]
        [DataRow("Dark")]
        [DataRow("Light")]
        public void BodyTextMeetsAaOnEverySurface(string palette)
        {
            RunSta(() =>
            {
                var d = Dictionary($"Themes/Palette.{palette}.xaml");
                var failures = new List<string>();
                foreach (string surface in Surfaces)
                {
                    Color bg = Solid(d, surface, Colors.White);
                    foreach (string text in BodyText.Concat(StatusText))
                        Check(failures, $"{text} on {surface}", Solid(d, text, bg), bg);
                    // Hover/sunken fills are translucent: text must still pass on top of them.
                    Color sunken = Solid(d, "SurfaceSunkenBrush", bg);
                    Check(failures, $"TextSecondaryBrush on SurfaceSunkenBrush over {surface}", Solid(d, "TextSecondaryBrush", sunken), sunken);
                }
                Color surfaceColor = Solid(d, "SurfaceBrush", Colors.White);
                foreach (var (text, tint) in new[] { ("SuccessBrush", "SuccessLightBrush"), ("WarnBrush", "WarnLightBrush"),
                                                     ("ErrorBrush", "ErrorLightBrush"), ("InfoBrush", "InfoLightBrush"),
                                                     ("AccentTextBrush", "AccentLightBrush"), ("TextBrush", "AccentLightBrush") })
                {
                    Color under = Solid(d, tint, surfaceColor);
                    Check(failures, $"{text} on {tint}", Solid(d, text, under), under);
                }
                Check(failures, "TextOnAccentBrush on AccentFillBrush", Solid(d, "TextOnAccentBrush", Colors.Black), Solid(d, "AccentFillBrush", Colors.Black));
                Check(failures, "TextOnAccentBrush on AccentFillHoverBrush", Solid(d, "TextOnAccentBrush", Colors.Black), Solid(d, "AccentFillHoverBrush", Colors.Black));
                Color tip = Solid(d, "TooltipBgBrush", Colors.Black);
                Check(failures, "TooltipTextBrush on TooltipBgBrush", Solid(d, "TooltipTextBrush", tip), tip);
                Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
            });
        }

        [TestMethod]
        public void BrandGradientCarriesWhiteText()
        {
            RunSta(() =>
            {
                var tokens = Dictionary("Themes/Tokens.xaml");
                var gradient = (LinearGradientBrush)tokens["BrandGradientBrush"];
                foreach (var stop in gradient.GradientStops)
                    Assert.IsTrue(Contrast(Colors.White, stop.Color) >= 4.5, $"white on {stop.Color}: {Contrast(Colors.White, stop.Color):F2}");
            });
        }

        /// <summary>
        /// The primary action button (Start's "Uruchom", sign-in, setup's last step) takes the brand gradient from the
        /// palettes so that high contrast can map it. The 2.5.0 live look on High Contrast Black found the token gradient
        /// kept under the system's black highlight text (3.7:1). Both palettes carry the token's stops, white text keeps
        /// 4.5:1 on every stop of the fill and of its hover, and the high-contrast palette makes both the system highlight.
        /// </summary>
        [TestMethod]
        public void PrimaryActionFillIsTheBrandGradientAndHighContrastMapsIt()
        {
            RunSta(() =>
            {
                var token = (LinearGradientBrush)Dictionary("Themes/Tokens.xaml")["BrandGradientBrush"];
                var tokenStops = token.GradientStops.Select(s => s.Color).ToList();
                foreach (string palette in new[] { "Dark", "Light" })
                {
                    var d = Dictionary($"Themes/Palette.{palette}.xaml");
                    var fill = d["AccentFillHeroBrush"] as LinearGradientBrush;
                    Assert.IsNotNull(fill, $"{palette}: AccentFillHeroBrush is the brand gradient");
                    CollectionAssert.AreEqual(tokenStops, fill.GradientStops.Select(s => s.Color).ToList(), $"{palette}: the button wears the token's gradient");
                    foreach (string key in new[] { "AccentFillHeroBrush", "AccentFillHeroHoverBrush" })
                        foreach (var stop in ((LinearGradientBrush)d[key]).GradientStops)
                            Assert.IsTrue(Contrast(Colors.White, stop.Color) >= 4.5, $"{palette} {key}: white on {stop.Color}: {Contrast(Colors.White, stop.Color):F2}");
                }
                var hc = ThemeService.BuildHighContrastPalette(Dictionary("Themes/Palette.Light.xaml"));
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)hc["AccentFillHeroBrush"]).Color);
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)hc["AccentFillHeroHoverBrush"]).Color);
            });
        }

        [TestMethod]
        [DataRow(ThemePreference.Auto, null, false, AppTheme.Dark)]
        [DataRow(ThemePreference.Auto, true, false, AppTheme.Light)]
        [DataRow(ThemePreference.Auto, false, false, AppTheme.Dark)]
        [DataRow(ThemePreference.Dark, true, false, AppTheme.Dark)]
        [DataRow(ThemePreference.Light, false, false, AppTheme.Light)]
        [DataRow(ThemePreference.Light, true, true, AppTheme.HighContrast)]
        [DataRow(ThemePreference.Dark, null, true, AppTheme.HighContrast)]
        public void ResolveFollowsPreferenceWindowsAndHighContrast(ThemePreference pref, bool? windowsLight, bool highContrast, AppTheme expected)
        {
            Assert.AreEqual(expected, ThemeService.Resolve(pref, windowsLight, highContrast));
        }

        [TestMethod]
        [DataRow("Dark", ThemePreference.Dark)]
        [DataRow("light", ThemePreference.Light)]
        [DataRow("AUTO", ThemePreference.Auto)]
        [DataRow(null, ThemePreference.Auto)]
        [DataRow("", ThemePreference.Auto)]
        [DataRow("Sepia", ThemePreference.Auto)]
        [DataRow("7", ThemePreference.Auto)]
        public void ParseFallsBackToAuto(string value, ThemePreference expected)
        {
            Assert.AreEqual(expected, ThemeService.Parse(value));
        }

        [TestMethod]
        public void ApplyToSwapsThePaletteInPlaceAndUpdatesDynamicReferences()
        {
            RunSta(() =>
            {
                var root = new ResourceDictionary();
                root.MergedDictionaries.Add(Dictionary("Themes/Tokens.xaml"));
                var strings = new ResourceDictionary { ["Some_String"] = "x" };
                root.MergedDictionaries.Add(strings);
                var dark = Dictionary("Themes/Palette.Dark.xaml");
                var light = Dictionary("Themes/Palette.Light.xaml");

                ThemeService.ApplyTo(root, dark);
                Assert.AreEqual(1, root.MergedDictionaries.IndexOf(dark), "palette goes right after Tokens");

                var host = new System.Windows.Controls.Border();
                host.Resources = root;
                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SurfaceBrush");
                Assert.AreEqual(((SolidColorBrush)dark["SurfaceBrush"]).Color, ((SolidColorBrush)host.Background).Color);

                ThemeService.ApplyTo(root, light);
                Assert.AreEqual(3, root.MergedDictionaries.Count, "replaced, not added");
                Assert.AreSame(light, root.MergedDictionaries[1]);
                Assert.AreSame(strings, root.MergedDictionaries[2]);
                Assert.AreEqual(((SolidColorBrush)light["SurfaceBrush"]).Color, ((SolidColorBrush)host.Background).Color,
                    "a DynamicResource consumer follows the swap without being recreated");
            });
        }

        [TestMethod]
        public void HighContrastPaletteMapsEveryKeyToSystemColours()
        {
            RunSta(() =>
            {
                var light = Dictionary("Themes/Palette.Light.xaml");
                var hc = ThemeService.BuildHighContrastPalette(light);
                var systemColours = new HashSet<Color>(typeof(SystemColors).GetProperties()
                    .Where(p => p.PropertyType == typeof(Color)).Select(p => (Color)p.GetValue(null)));
                foreach (string key in Keys(light))
                {
                    Assert.IsTrue(hc.Contains(key), $"{key} missing from the high-contrast palette");
                    Color c = hc[key] is SolidColorBrush b ? b.Color : (Color)hc[key];
                    Assert.IsTrue(systemColours.Contains(c), $"{key} = {c} is not a Windows system colour");
                }
                Assert.AreEqual(SystemColors.WindowTextColor, ((SolidColorBrush)hc["TextBrush"]).Color);
                Assert.AreEqual(SystemColors.WindowColor, ((SolidColorBrush)hc["SurfaceBrush"]).Color);
                Assert.AreEqual(SystemColors.HighlightColor, ((SolidColorBrush)hc["AccentFillBrush"]).Color);
                Assert.AreEqual(SystemColors.HighlightTextColor, ((SolidColorBrush)hc["TextOnAccentBrush"]).Color);
                Assert.IsTrue(ThemeService.IsPalette(hc), "the slot recognises the generated palette for the next swap");
            });
        }

        [TestMethod]
        [DataRow("_Start", "Start")]
        [DataRow("S_króty", "Skróty")]
        [DataRow("Game _& subtitles", "Game & subtitles")]
        [DataRow("No marker", "No marker")]
        [DataRow("A__B _C", "A_B C")]
        [DataRow(null, null)]
        public void AccessKeyMarkersAreStrippedForTooltips(string header, string expected)
        {
            Assert.AreEqual(expected, AccessKeyTextConverter.Strip(header));
        }

        [TestMethod]
        [DataRow(1280.0, 1000.0, false)]
        [DataRow(1000.0, 1000.0, false)]
        [DataRow(999.0, 1000.0, true)]
        [DataRow(0.0, 1000.0, false)]
        [DataRow(800.0, double.NaN, false)]
        public void RailCollapsesUnderTheThreshold(double width, double threshold, bool compact)
        {
            Assert.AreEqual(compact, NavRail.ShouldBeCompact(width, threshold));
        }

        // ── helpers ──

        private static List<string> Keys(ResourceDictionary d) => d.Keys.OfType<string>().OrderBy(k => k).ToList();

        private static Color Solid(ResourceDictionary d, string key, Color under)
        {
            object v = d[key];
            Color c = v is SolidColorBrush b ? b.Color : v is Color col ? col : throw new AssertFailedException($"{key} is not a colour");
            return Over(c, under);
        }

        private static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            byte Mix(byte t, byte u) => (byte)Math.Round(a * t + (1 - a) * u);
            return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
        }

        private static void Check(List<string> failures, string what, Color fg, Color bg)
        {
            double ratio = Contrast(fg, bg);
            if (ratio < 4.5) failures.Add($"{what}: {ratio:F2}:1 ({fg} on {bg})");
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            static double Lin(byte v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
    }
}
