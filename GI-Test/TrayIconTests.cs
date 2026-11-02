using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using GI_Subtitles.Core.UI;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using Drawing = System.Drawing;
using Media = System.Windows.Media;

namespace GI_Test
{
    /// <summary>
    /// P3: the tray icon's states in the status chip's colours (stopped, translating, waiting or getting ready, needs
    /// attention), drawn sharp at every small-icon size, and the app icons carrying a frame for every Windows scale.
    /// </summary>
    [TestClass]
    public class TrayIconTests
    {
        private static readonly int[] TraySizes = { 16, 20, 24, 32 };

        [TestMethod]
        public void TrayStates_MatchTheStatusChip()
        {
            // The chip's dot for each Start state, read through the same code the window runs.
            RunSta(() =>
            {
                foreach (StartCardKind kind in Enum.GetValues(typeof(StartCardKind)))
                {
                    var dot = new Ellipse();
                    dot.Resources["SuccessBrush"] = Media.Brushes.Green;
                    dot.Resources["WarnBrush"] = Media.Brushes.Orange;
                    dot.Resources["ErrorBrush"] = Media.Brushes.Red;
                    dot.Resources["TextTertiaryBrush"] = Media.Brushes.Gray;
                    object Find(string name) => name == "StateChipDot" ? dot : null;
                    SettingsWindow.ApplyStateChipTo(Find, new StartCardState { Kind = kind }, (k, f) => f);
                    var chip = dot.Fill == Media.Brushes.Green ? TrayIcons.Success
                        : dot.Fill == Media.Brushes.Orange ? TrayIcons.Warn
                        : dot.Fill == Media.Brushes.Red ? TrayIcons.Error : (Drawing.Color?)null;
                    var tray = SettingsWindow.TrayStateFor(kind);
                    Assert.AreEqual(chip, TrayIcons.DotFor(tray), $"{kind}: the tray's dot is the chip's ({tray})");
                }
            });
        }

        [TestMethod]
        public void TrayIcons_TileAndDot_AtEverySmallIconSize()
        {
            foreach (TrayState state in Enum.GetValues(typeof(TrayState)))
            foreach (int size in TraySizes)
            {
                using var bmp = TrayIcons.Render(state, size);
                // The tile, left of the K: the brand gradient while Kaption is on, grey when stopped or stuck.
                var tile = bmp.GetPixel((int)(size * 0.12), size / 2);
                if (TrayIcons.BrandTile(state)) Assert.IsTrue(tile.B > tile.R + 40, $"{state} {size}px: brand tile, got {tile}");
                else Assert.IsTrue(Math.Abs(tile.R - TrayIcons.Idle.R) < 12 && Math.Abs(tile.B - TrayIcons.Idle.B) < 12, $"{state} {size}px: grey tile, got {tile}");

                var centre = TrayIcons.DotCentre(size);
                var px = bmp.GetPixel(Math.Min(size - 1, centre.X), Math.Min(size - 1, centre.Y));
                if (TrayIcons.DotFor(state) is Drawing.Color dot)
                    Assert.IsTrue(Math.Abs(px.R - dot.R) + Math.Abs(px.G - dot.G) + Math.Abs(px.B - dot.B) < 40, $"{state} {size}px: the chip's dot, got {px}");
                else
                    Assert.IsFalse(Near(px, TrayIcons.Success) || Near(px, TrayIcons.Warn) || Near(px, TrayIcons.Error), $"{state} {size}px: no dot");
            }
        }

        private static bool Near(Drawing.Color a, Drawing.Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 40;

        [TestMethod]
        public void AppIcons_HaveAFrameForEveryWindowsScale()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            foreach (string name in new[] { "kaption.ico", "kaption-running.ico" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(root, "GI-Subtitles", "Resources", name));
                int n = BitConverter.ToUInt16(b, 4);
                var sizes = Enumerable.Range(0, n).Select(i => b[6 + 16 * i] == 0 ? 256 : b[6 + 16 * i]).OrderBy(s => s).ToArray();
                CollectionAssert.AreEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 }, sizes, name);
            }
        }

        [TestMethod]
        public void RendersTheTrayStates_OnLightAndDarkTaskbars()
        {
            string path = null;
            RunSta(() =>
            {
                var states = (TrayState[])Enum.GetValues(typeof(TrayState));
                const int scale = 4, gap = 12;
                int width = gap + TraySizes.Sum(s => s * scale / 2 + gap) + 120;
                int band = states.Length * (32 * scale / 2 + gap) + gap;
                using var sheet = new Drawing.Bitmap(width, 2 * band + gap);
                using (var g = Drawing.Graphics.FromImage(sheet))
                {
                    g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
                    int y = gap;
                    foreach (var taskbar in new[] { Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3), Drawing.Color.FromArgb(0x1F, 0x1F, 0x1F) })
                    {
                        using (var bg = new Drawing.SolidBrush(taskbar))
                            g.FillRectangle(bg, 0, y - gap / 2, sheet.Width, states.Length * (32 * scale / 2 + gap) + gap);
                        foreach (var state in states)
                        {
                            int x = gap;
                            foreach (int size in TraySizes)
                            {
                                using var icon = TrayIcons.Render(state, size);
                                g.DrawImage(icon, x, y, size * scale / 2, size * scale / 2);
                                x += size * scale / 2 + gap;
                            }
                            y += 32 * scale / 2 + gap;
                        }
                        y += gap;
                    }
                }
                using var ms = new MemoryStream();
                sheet.Save(ms, Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = ms;
                image.EndInit();
                image.Freeze();
                path = SaveReview(image, "tray/tray-states.png");
            });
            Console.WriteLine(path);
        }
    }
}
