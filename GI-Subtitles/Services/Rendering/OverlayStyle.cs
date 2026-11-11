using System;
using System.Windows;
using GI_Subtitles.Core.Config;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>The three looks on Wygląd napisów. Picking one writes its values; changing a value afterwards is "custom".</summary>
    public enum OverlayPreset
    {
        /// <summary>Klasyczny: the 2.5 look (size 22, 69 % panel) with the 2.6 soft outline.</summary>
        Classic,
        /// <summary>Kontrast: a heavier outline and a darker panel, for bright scenes.</summary>
        Contrast,
        /// <summary>Kino: larger, wider letter spacing, 70 % panel, low and centred above the game's dialogue.</summary>
        Cinema,
    }

    /// <summary>
    /// What the subtitle looks like and where it sits, read from Config. The pre-2.6 keys keep their names and meaning
    /// (Size, SubtitleBgOpacity, Pad, FontFamily), so a saved size, position and panel look the same after the update;
    /// 2.6 adds Overlay.Font, Overlay.Outline, Overlay.LetterSpacing, Overlay.AutoDarken and Overlay.Mark. Pure apart from
    /// <see cref="FromConfig"/> and <see cref="Save"/>; every derived size scales with <see cref="Size"/> from its value at 22.
    /// </summary>
    public sealed record OverlayStyle
    {
        public const string SizeKey = "Size";
        public const string OpacityKey = "SubtitleBgOpacity";
        public const string PadKey = "Pad";
        /// <summary>The 2.4/2.5 font key. Read only when <see cref="FontKey"/> is absent, and "Segoe UI" there means the old default.</summary>
        public const string LegacyFontKey = "FontFamily";
        public const string FontKey = "Overlay.Font";
        public const string OutlineKey = "Overlay.Outline";
        public const string LetterSpacingKey = "Overlay.LetterSpacing";
        public const string AutoDarkenKey = "Overlay.AutoDarken";
        public const string MarkKey = "Overlay.Mark";

        public const int MinSize = 10, MaxSize = 36;
        public const int MinPad = -500, MaxPad = 500;
        /// <summary>Kino sits this far above the game's dialogue area.</summary>
        public const double CinemaGap = 16;
        /// <summary>MainWindow makes the window 10 DIPs taller than the panel and centres the panel: 5 above, 5 below.</summary>
        public const double WindowMargin = 5;

        /// <summary>Subtitle size in DIPs as the user set it (the face scale is applied when drawing).</summary>
        public int Size { get; init; } = SubtitleOverlayDefaults.FontSize;
        public string FontFamily { get; init; } = SubtitleOverlayDefaults.FontFamily;
        /// <summary>Panel alpha, 0-255.</summary>
        public int BackgroundOpacity { get; init; } = SubtitleOverlayDefaults.BackgroundOpacity;
        /// <summary>Outline width in DIPs at size 22 (1 = soft, 2 = Kontrast).</summary>
        public double Outline { get; init; } = 1.0;
        /// <summary>Extra letter spacing in em (Kino 0.03).</summary>
        public double LetterSpacing { get; init; }
        /// <summary>Darken the panel when the scene behind it is bright.</summary>
        public bool AutoDarken { get; init; } = true;
        /// <summary>The small Kaption mark at the end of the band, for recordings and streams. Off unless the player asks.</summary>
        public bool Mark { get; init; }
        /// <summary>Offset of the panel's top from the dialogue area's top, in DIPs; null = the game's default.</summary>
        public int? PadVertical { get; init; }
        public int PadHorizontal { get; init; }

        public static OverlayStyle Default { get; } = new();

        // ── Derived (everything scales from its value at size 22) ─────────────

        public double Scale => Size / 22.0;
        /// <summary>The em size drawn: the saved size times the face's scale, so 22 looks like 22 in every face.</summary>
        public double RenderSize => Size * OverlayFonts.FaceScale(FontFamily);
        /// <summary>OutlinedTextBlock.StrokeThickness: the pen is twice this and half of it sits under the fill.</summary>
        public double StrokeThickness => Math.Round(Outline * Scale, 2);
        public double ShadowOffset => Math.Round(2 * Scale, 2);
        /// <summary>Optical size: small text a step heavier, the rest semibold.</summary>
        public FontWeight Weight => Size < 20 ? FontWeights.Bold : FontWeights.SemiBold;
        /// <summary>Optical size: small text a little looser, large text a little tighter, plus the preset's spacing.</summary>
        public double Tracking => (Size < 18 ? 0.012 : Size > 30 ? -0.006 : 0) + LetterSpacing;
        public double HeaderSize => Math.Max(11, Math.Round(Size * 0.68));
        public double AnswerSize => Math.Max(12, Size - 3);
        /// <summary>The panel's padding without the mark (the press kit reads these four numbers).</summary>
        private Thickness BasePadding => new(18 * Scale, 10 * Scale, 18 * Scale, 12 * Scale);
        /// <summary>With the mark on, both sides are at least the mark's column, so the text stays centred and clear of it.</summary>
        public Thickness Padding
        {
            get
            {
                var pad = BasePadding;
                if (!Mark) return pad;
                double side = Math.Max(pad.Left, KaptionMarkLayout.Column(Size));
                return new(side, pad.Top, side, pad.Bottom);
            }
        }
        public double CornerRadius => 10 * Scale;

        // ── Presets ──────────────────────────────────────────────────────────

        public static OverlayStyle PresetValues(OverlayPreset preset) => preset switch
        {
            OverlayPreset.Contrast => new OverlayStyle { Size = 22, BackgroundOpacity = 230, Outline = 2.0, LetterSpacing = 0 },
            OverlayPreset.Cinema => new OverlayStyle { Size = 26, BackgroundOpacity = 179, Outline = 1.0, LetterSpacing = 0.03 },
            _ => new OverlayStyle { Size = 22, BackgroundOpacity = 176, Outline = 1.0, LetterSpacing = 0 },
        };

        /// <summary>
        /// This style with a preset's look. Position stays, except Kino: centred, and low enough that the panel's bottom
        /// sits <see cref="CinemaGap"/> above the dialogue area. <paramref name="referenceWindowHeight"/> is the overlay
        /// window's height for a one-line subtitle with a name at Kino's size (the panel plus the window margins).
        /// </summary>
        public OverlayStyle WithPreset(OverlayPreset preset, double referenceWindowHeight = 0)
        {
            var p = PresetValues(preset);
            var next = this with { Size = p.Size, BackgroundOpacity = p.BackgroundOpacity, Outline = p.Outline, LetterSpacing = p.LetterSpacing };
            if (preset == OverlayPreset.Cinema && referenceWindowHeight > 0)
                next = next with { PadHorizontal = 0, PadVertical = SnugPad(referenceWindowHeight) };
            return next;
        }

        /// <summary>
        /// The offset that puts the panel's bottom <see cref="CinemaGap"/> above the dialogue area: the window's bottom (one
        /// margin below the panel) lands at regionTop - gap + margin.
        /// </summary>
        public static int SnugPad(double referenceWindowHeight) =>
            -(int)Math.Round(CinemaGap + referenceWindowHeight - WindowMargin);

        /// <summary>The preset whose look this is, or null when a value was changed by hand. Position is not part of a look.</summary>
        public OverlayPreset? MatchingPreset()
        {
            foreach (OverlayPreset preset in Enum.GetValues(typeof(OverlayPreset)))
            {
                var p = PresetValues(preset);
                if (p.Size == Size && p.BackgroundOpacity == BackgroundOpacity && Math.Abs(p.Outline - Outline) < 0.01
                    && Math.Abs(p.LetterSpacing - LetterSpacing) < 0.001)
                    return preset;
            }
            return null;
        }

        // ── Config ───────────────────────────────────────────────────────────

        /// <summary>
        /// The saved style. Missing keys take the defaults; "Segoe UI" under the old font key is the pre-2.6 default
        /// (every 2.5 launch wrote it, chosen or not), so it reads as the new default face.
        /// </summary>
        public static OverlayStyle FromConfig()
        {
            string font = Config.Has(FontKey) ? Config.Get<string>(FontKey, null) : null;
            if (string.IsNullOrWhiteSpace(font))
            {
                string legacy = Config.Get<string>(LegacyFontKey, null);
                font = string.IsNullOrWhiteSpace(legacy) || string.Equals(legacy.Trim(), SubtitleOverlayDefaults.LegacyFontFamily, StringComparison.OrdinalIgnoreCase)
                    ? SubtitleOverlayDefaults.FontFamily
                    : legacy.Trim();
            }
            return new OverlayStyle
            {
                Size = Math.Clamp(Config.Get(SizeKey, SubtitleOverlayDefaults.FontSize), MinSize, MaxSize),
                FontFamily = font,
                BackgroundOpacity = Math.Clamp(Config.Get(OpacityKey, SubtitleOverlayDefaults.BackgroundOpacity), 0, 255),
                Outline = Math.Clamp(Config.Get(OutlineKey, 1.0), 0, 4),
                LetterSpacing = Math.Clamp(Config.Get(LetterSpacingKey, 0.0), -0.05, 0.12),
                AutoDarken = Config.Get(AutoDarkenKey, true),
                Mark = Config.Get(MarkKey, false),
                PadVertical = Config.Has(PadKey) ? Config.GetPad(0) : null,
                PadHorizontal = Config.GetPadHorizontal(0),
            };
        }

        /// <summary>Writes what differs from <paramref name="saved"/>; position only when it changed.</summary>
        public void Save(OverlayStyle saved)
        {
            saved ??= FromConfig();
            if (Size != saved.Size) Config.Set(SizeKey, Size);
            if (BackgroundOpacity != saved.BackgroundOpacity) Config.Set(OpacityKey, BackgroundOpacity);
            if (Math.Abs(Outline - saved.Outline) > 0.001) Config.Set(OutlineKey, Outline);
            if (Math.Abs(LetterSpacing - saved.LetterSpacing) > 0.0001) Config.Set(LetterSpacingKey, LetterSpacing);
            if (AutoDarken != saved.AutoDarken) Config.Set(AutoDarkenKey, AutoDarken);
            if (Mark != saved.Mark) Config.Set(MarkKey, Mark);
            if (!string.Equals(FontFamily, saved.FontFamily, StringComparison.Ordinal)) Config.Set(FontKey, FontFamily);
            if (PadVertical != saved.PadVertical || PadHorizontal != saved.PadHorizontal)
            {
                if (PadVertical.HasValue) Config.Set(PadKey, new[] { PadVertical.Value, PadHorizontal });
                else Config.Remove(PadKey);
            }
        }

        /// <summary>"Przywróć domyślne": every key this page owns goes back to its default (the game's own position).</summary>
        public static void ResetConfig()
        {
            foreach (string key in new[] { SizeKey, OpacityKey, OutlineKey, LetterSpacingKey, AutoDarkenKey, MarkKey, FontKey, LegacyFontKey, PadKey,
                         "MaxOverlayWidth", "MaxOverlayHeight", "AutoShrinkText" })
                if (Config.Has(key)) Config.Remove(key);
        }
    }
}
