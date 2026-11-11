using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// The subtitle panel: a dark rounded panel with the speaker as a small tinted tag, the line under it (at most two
    /// balanced lines) and, when answer translation is on, the answers. The overlay window and the Settings preview both
    /// use this control, styled by <see cref="ApplyStyle"/>, so the preview shows exactly what the game will.
    /// <para>The panel is drawn by this control rather than by Border: when a new line changes its size it follows in
    /// <see cref="SubtitleOverlayDefaults.ResizeMs"/> from the edge nearest the game's dialogue instead of jumping, and its
    /// tint darkens over bright scenes (<see cref="BackdropLuminance"/>). Part names are kept (SubtitleText, HeaderText,
    /// AnswerText, AnswerSeparator) so code that looks them up by name, like the promo renderer, still finds them.</para>
    /// </summary>
    public class SubtitleCard : Border
    {
        public OutlinedTextBlock HeaderText { get; }
        public Border HeaderPill { get; }
        public OutlinedTextBlock SubtitleText { get; }
        public Border AnswerSeparator { get; }
        public OutlinedTextBlock AnswerText { get; }

        /// <summary>The panel's drawn size while it follows a size change (NaN = the control's own size).</summary>
        public static readonly DependencyProperty PanelWidthProperty = DependencyProperty.Register(
            nameof(PanelWidth), typeof(double), typeof(SubtitleCard),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty PanelHeightProperty = DependencyProperty.Register(
            nameof(PanelHeight), typeof(double), typeof(SubtitleCard),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Brightness of the scene behind the panel, 0 (dark) to 1 (bright); the panel darkens above about 0.55.</summary>
        public static readonly DependencyProperty BackdropLuminanceProperty = DependencyProperty.Register(
            nameof(BackdropLuminance), typeof(double), typeof(SubtitleCard),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double PanelWidth { get => (double)GetValue(PanelWidthProperty); set => SetValue(PanelWidthProperty, value); }
        public double PanelHeight { get => (double)GetValue(PanelHeightProperty); set => SetValue(PanelHeightProperty, value); }
        public double BackdropLuminance { get => (double)GetValue(BackdropLuminanceProperty); set => SetValue(BackdropLuminanceProperty, value); }

        /// <summary>The panel grows upward (bottom edge fixed), as when it sits above the game's dialogue.</summary>
        public bool GrowsUpward { get; set; } = true;
        /// <summary>Follow size changes smoothly (off in the Settings preview's static renders and when motion is reduced).</summary>
        public bool SmoothResize { get; set; } = true;
        public bool AutoDarken { get; private set; } = true;
        public OverlayStyle Look { get; private set; } = OverlayStyle.Default;

        private Color _speakerHue = Colors.Transparent;
        private bool _hasSpeakerHue;
        private Size _lastSize;

        public SubtitleCard()
        {
            Cursor = Cursors.Hand;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;
            Background = OverlayTokens.Brush(OverlayTokens.Panel, (byte)SubtitleOverlayDefaults.BackgroundOpacity);

            HeaderText = new OutlinedTextBlock
            {
                Name = "HeaderText",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            HeaderPill = new Border
            {
                Name = "HeaderPill",
                Child = HeaderText,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true,
            };
            SubtitleText = new OutlinedTextBlock
            {
                Name = "SubtitleText",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BalanceLines = true,
            };
            AnswerSeparator = new Border
            {
                Name = "AnswerSeparator",
                Height = 1,
                Visibility = Visibility.Collapsed,
                Background = OverlayTokens.Brush(OverlayTokens.Separator),
            };
            AnswerText = new OutlinedTextBlock
            {
                Name = "AnswerText",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                FontWeight = FontWeights.Normal,
                Visibility = Visibility.Collapsed,
                Foreground = OverlayTokens.Brush(OverlayTokens.Answer),
                StrokeBrush = OverlayTokens.Brush(Color.FromArgb(0x50, 0, 0, 0)),
            };

            var stack = new StackPanel();
            stack.Children.Add(HeaderPill);
            stack.Children.Add(SubtitleText);
            stack.Children.Add(AnswerSeparator);
            stack.Children.Add(AnswerText);
            Child = stack;

            // The tag shows only while the name is shown and has text (callers toggle HeaderText, as before).
            var visibility = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(OutlinedTextBlock));
            var text = DependencyPropertyDescriptor.FromProperty(OutlinedTextBlock.TextProperty, typeof(OutlinedTextBlock));
            visibility.AddValueChanged(HeaderText, (_, __) => SyncHeaderPill());
            text.AddValueChanged(HeaderText, (_, __) => SyncHeaderPill());

            ApplyStyle(OverlayStyle.Default);
        }

        private void SyncHeaderPill() =>
            HeaderPill.Visibility = HeaderText.Visibility == Visibility.Visible && !string.IsNullOrEmpty(HeaderText.Text)
                ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Face, sizes, outline, shadow, spacing, panel tint and padding from a style.</summary>
        public void ApplyStyle(OverlayStyle style)
        {
            style ??= OverlayStyle.Default;
            Look = style;
            AutoDarken = style.AutoDarken;
            var family = OverlayFonts.Resolve(style.FontFamily);
            var outline = OverlayTokens.Brush(OverlayTokens.Outline);
            var shadow = OverlayTokens.Brush(OverlayTokens.Shadow);

            SubtitleText.FontFamily = family;
            SubtitleText.FontWeight = style.Weight;
            SubtitleText.LetterSpacing = style.Tracking;
            SubtitleText.StrokeBrush = outline;
            SubtitleText.StrokeThickness = style.StrokeThickness;
            SubtitleText.ShadowBrush = shadow;
            SubtitleText.ShadowOffset = style.ShadowOffset;
            SubtitleText.Foreground = OverlayTokens.Brush(OverlayTokens.Text);

            HeaderText.FontFamily = family;
            HeaderText.FontWeight = FontWeights.SemiBold;
            HeaderText.LetterSpacing = 0.01 + style.LetterSpacing / 2;
            HeaderText.StrokeBrush = outline;
            HeaderText.StrokeThickness = Math.Round(style.StrokeThickness * 0.6, 2);
            HeaderText.ShadowBrush = null;

            AnswerText.FontFamily = family;
            AnswerText.StrokeThickness = Math.Max(0.6, style.StrokeThickness * 1.2);

            Padding = style.Padding;
            CornerRadius = new CornerRadius(style.CornerRadius);
            HeaderPill.Padding = new Thickness(8 * style.Scale, 1 * style.Scale, 8 * style.Scale, 2 * style.Scale);
            HeaderPill.Margin = new Thickness(0, 0, 0, 6 * style.Scale);
            AnswerSeparator.Margin = new Thickness(0, 8 * style.Scale, 0, 6 * style.Scale);
            Background = OverlayTokens.Brush(OverlayTokens.Panel, (byte)Math.Clamp(style.BackgroundOpacity, 0, 255));
            ApplySpeakerTint();
            SetTextSize(style.Size);
        }

        /// <summary>
        /// The size the text is drawn at (the saved size, or what auto-shrink chose); the face scale and the tag's size
        /// follow from it.
        /// </summary>
        public void SetTextSize(double size)
        {
            double face = OverlayFonts.FaceScale(Look.FontFamily);
            SubtitleText.FontSize = Math.Max(1, size * face);
            HeaderText.FontSize = Math.Max(11, Math.Round(size * 0.68)) * face;
            AnswerText.FontSize = Math.Max(12, size - 3) * face;
        }

        /// <summary>
        /// The speaker's tag colour: the element's hue when the app knows the speaker, else the neutral tag (null).
        /// </summary>
        public void SetSpeakerHue(Color? hue)
        {
            _hasSpeakerHue = hue.HasValue;
            _speakerHue = hue ?? Colors.Transparent;
            ApplySpeakerTint();
        }

        private void ApplySpeakerTint()
        {
            if (_hasSpeakerHue)
            {
                var h = _speakerHue;
                HeaderPill.Background = OverlayTokens.Brush(h, 0x3D);
                HeaderPill.BorderBrush = OverlayTokens.Brush(h, 0x66);
                HeaderPill.BorderThickness = new Thickness(1);
                // The hue lifted towards white, for text on the dark panel.
                HeaderText.Foreground = OverlayTokens.Brush(Color.FromRgb(
                    (byte)(h.R + (255 - h.R) * 0.45), (byte)(h.G + (255 - h.G) * 0.45), (byte)(h.B + (255 - h.B) * 0.45)));
            }
            else
            {
                HeaderPill.Background = OverlayTokens.Brush(OverlayTokens.NameFill);
                HeaderPill.BorderBrush = null;
                HeaderPill.BorderThickness = new Thickness(0);
                HeaderText.Foreground = OverlayTokens.Brush(OverlayTokens.NameText);
            }
            double pillHeight = HeaderText.FontSize * 1.4 + HeaderPill.Padding.Top + HeaderPill.Padding.Bottom;
            HeaderPill.CornerRadius = new CornerRadius(Math.Max(4, pillHeight / 2));
        }

        /// <summary>The panel colour actually drawn: the saved tint, darkened over bright scenes when allowed.</summary>
        public Brush EffectivePanelBrush()
        {
            if (Background is not SolidColorBrush solid) return Background;
            if (!AutoDarken) return solid;
            byte a = DarkenedAlpha(solid.Color.A, BackdropLuminance);
            return a == solid.Color.A ? solid : OverlayTokens.Brush(Color.FromArgb(a, solid.Color.R, solid.Color.G, solid.Color.B));
        }

        /// <summary>
        /// The panel's alpha over a scene of <paramref name="luminance"/> (0-1): unchanged on dark scenes, rising smoothly
        /// to 232 (91 %) over bright ones, never lighter than the saved tint.
        /// </summary>
        public static byte DarkenedAlpha(byte saved, double luminance)
        {
            const double from = 0.55, to = 0.85, max = 232;
            if (saved >= max || double.IsNaN(luminance)) return saved;
            double t = Math.Clamp((luminance - from) / (to - from), 0, 1);
            t = t * t * (3 - 2 * t);
            return (byte)Math.Round(saved + (max - saved) * t);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = RenderSize.Width, h = RenderSize.Height;
            if (w <= 0 || h <= 0) return;
            double pw = double.IsNaN(PanelWidth) ? w : PanelWidth;
            double ph = double.IsNaN(PanelHeight) ? h : PanelHeight;
            var rect = new Rect((w - pw) / 2, GrowsUpward ? h - ph : 0, Math.Max(0, pw), Math.Max(0, ph));
            var drawn = rect;
            double r = Math.Min(CornerRadius.TopLeft, Math.Min(rect.Width, rect.Height) / 2);
            Pen pen = null;
            if (BorderBrush != null && BorderThickness.Left > 0)
            {
                double t = BorderThickness.Left;
                pen = new Pen(BorderBrush, t);
                rect.Inflate(-t / 2, -t / 2);
            }
            dc.DrawRoundedRectangle(EffectivePanelBrush(), pen, rect, r, r);
            if (Look.Mark) DrawMark(dc, drawn);
        }

        /// <summary>
        /// The Kaption mark at the end of the band: part of the panel, so it moves, fades and is masked with it. Its
        /// place comes from the layout the text already has; while the panel is still growing towards that layout
        /// (<see cref="PanelWidth"/>) and does not reach the mark's place yet, the mark waits, so it is never over text.
        /// </summary>
        private void DrawMark(DrawingContext dc, Rect drawn)
        {
            var mark = OverlayTokens.Mark;
            var box = KaptionMarkLayout.Box(new Rect(RenderSize), Look.Size, Padding, SubtitleText.FontSize);
            if (mark == null || box.IsEmpty || !drawn.Contains(box)) return;
            double scale = box.Width / OverlayTokens.MarkUnits;
            dc.PushOpacity(KaptionMarkLayout.Opacity);
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, box.X, box.Y));
            dc.DrawDrawing(mark);
            dc.Pop();
            dc.Pop();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            var now = info.NewSize;
            var was = _lastSize;
            _lastSize = now;
            if (!SmoothResize || !MotionAllowed || was.Width <= 0 || was.Height <= 0 || !IsVisible || Opacity < 0.05)
            {
                BeginAnimation(PanelWidthProperty, null);
                BeginAnimation(PanelHeightProperty, null);
                return;
            }
            // Start from what is on screen now (an unfinished change included) and settle on the new size.
            double fromW = double.IsNaN(PanelWidth) ? was.Width : PanelWidth;
            double fromH = double.IsNaN(PanelHeight) ? was.Height : PanelHeight;
            Follow(PanelWidthProperty, fromW, now.Width);
            Follow(PanelHeightProperty, fromH, now.Height);
        }

        private void Follow(DependencyProperty property, double from, double to)
        {
            if (Math.Abs(from - to) < 0.5)
            {
                BeginAnimation(property, null);
                return;
            }
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SubtitleOverlayDefaults.ResizeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            animation.Freeze();
            BeginAnimation(property, animation);
        }

        /// <summary>Windows' "show animations" setting; off means every change lands at once.</summary>
        public static bool MotionAllowed => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

        /// <summary>A fade length, or 0 when Windows animations are off.</summary>
        public static TimeSpan Fade(int ms) => MotionAllowed ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

        /// <summary>
        /// The panel's height for a one-line subtitle with a speaker at <paramref name="style"/>: where a typical line
        /// sits, so a saved position keeps meaning the same place (see MainWindow's anchoring).
        /// </summary>
        public static double ReferenceHeight(OverlayStyle style, double pixelsPerDip = 1.0)
        {
            style ??= OverlayStyle.Default;
            var family = OverlayFonts.Resolve(style.FontFamily);
            double face = OverlayFonts.FaceScale(style.FontFamily);
            var line = OutlinedTextBlock.MeasureText("Ag", style.Size * face, family, style.Weight, FontStyles.Normal,
                double.PositiveInfinity, style.StrokeThickness, pixelsPerDip);
            double headerSize = Math.Max(11, Math.Round(style.Size * 0.68)) * face;
            var name = OutlinedTextBlock.MeasureText("Ag", headerSize, family, FontWeights.SemiBold, FontStyles.Normal,
                double.PositiveInfinity, Math.Round(style.StrokeThickness * 0.6, 2), pixelsPerDip);
            var pad = style.Padding;
            double tag = name.Height + (1 + 2) * style.Scale + 6 * style.Scale;
            return Math.Ceiling(pad.Top + tag + line.Height + pad.Bottom);
        }
    }
}
