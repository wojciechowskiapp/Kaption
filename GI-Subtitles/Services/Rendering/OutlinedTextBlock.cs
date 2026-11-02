using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// The overlay's text: outline geometry with a soft stroke and an optional drop shadow, so it stays readable on any
    /// scene. Layout comes from <see cref="OverlayTextLayout"/> (WPF shaping plus letter spacing and balanced lines), and
    /// the bundled faces resolve through <see cref="OverlayFonts"/>, so <c>new FontFamily("Source Sans 3")</c> works
    /// wherever this control is used (the overlay, the Settings preview, the promo renderer).
    /// </summary>
    public class OutlinedTextBlock : FrameworkElement
    {
        #region Dependency Properties

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                new FontFamily(SubtitleOverlayDefaults.FontFamily),
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(16.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontWeights.Bold,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontStyles.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontStretches.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
            nameof(StrokeBrush), typeof(Brush), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender, OnPenInvalidated));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(2.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
            nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(TextAlignment.Center,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
            nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(TextWrapping.Wrap,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        public static readonly DependencyProperty MaxTextWidthProperty = DependencyProperty.Register(
            nameof(MaxTextWidth), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(double.PositiveInfinity,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        /// <summary>Extra space between letters, in em (0 = the face's own spacing).</summary>
        public static readonly DependencyProperty LetterSpacingProperty = DependencyProperty.Register(
            nameof(LetterSpacing), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        /// <summary>Wrapped lines of near equal length instead of a full line and a stub.</summary>
        public static readonly DependencyProperty BalanceLinesProperty = DependencyProperty.Register(
            nameof(BalanceLines), typeof(bool), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnLayoutInvalidated));

        /// <summary>The drop under the outline (null = none).</summary>
        public static readonly DependencyProperty ShadowBrushProperty = DependencyProperty.Register(
            nameof(ShadowBrush), typeof(Brush), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPenInvalidated));

        /// <summary>How far the drop falls, in DIPs.</summary>
        public static readonly DependencyProperty ShadowOffsetProperty = DependencyProperty.Register(
            nameof(ShadowOffset), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPenInvalidated));

        #endregion

        #region CLR Properties

        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
        public FontStyle FontStyle { get => (FontStyle)GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }
        public FontStretch FontStretch { get => (FontStretch)GetValue(FontStretchProperty); set => SetValue(FontStretchProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
        public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
        public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
        public double MaxTextWidth { get => (double)GetValue(MaxTextWidthProperty); set => SetValue(MaxTextWidthProperty, value); }
        public double LetterSpacing { get => (double)GetValue(LetterSpacingProperty); set => SetValue(LetterSpacingProperty, value); }
        public bool BalanceLines { get => (bool)GetValue(BalanceLinesProperty); set => SetValue(BalanceLinesProperty, value); }
        public Brush ShadowBrush { get => (Brush)GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }
        public double ShadowOffset { get => (double)GetValue(ShadowOffsetProperty); set => SetValue(ShadowOffsetProperty, value); }

        /// <summary>Lines in the last layout (0 for empty text).</summary>
        public int LineCount => _layout?.Lines ?? 0;

        #endregion

        private OverlayTextBlockLayout _layout;
        private double _layoutWidth = double.NaN;
        private Pen _strokePen, _shadowPen;
        private double _constraintWidth;

        private static void OnLayoutInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (OutlinedTextBlock)d;
            control._layout = null;
            control._strokePen = null;
            control._shadowPen = null;
        }

        private static void OnPenInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (OutlinedTextBlock)d;
            control._strokePen = null;
            control._shadowPen = null;
        }

        /// <summary>The wrap width: MaxTextWidth clamped to the space the parent offers, minus the outline on both sides.</summary>
        private double WrapWidth()
        {
            if (TextWrapping == TextWrapping.NoWrap) return double.PositiveInfinity;
            double maxWidth = MaxTextWidth;
            if (_constraintWidth > 0 && !double.IsPositiveInfinity(_constraintWidth))
                maxWidth = double.IsPositiveInfinity(maxWidth) || maxWidth <= 0 ? _constraintWidth : Math.Min(maxWidth, _constraintWidth);
            if (maxWidth <= 0 || double.IsPositiveInfinity(maxWidth)) return double.PositiveInfinity;
            double effective = maxWidth - StrokeThickness * 2;
            return effective > 0 ? effective : maxWidth;
        }

        private OverlayTextBlockLayout EnsureLayout()
        {
            double wrap = WrapWidth();
            if (_layout != null && (wrap == _layoutWidth || (double.IsInfinity(wrap) && double.IsInfinity(_layoutWidth))))
                return _layout;
            _layoutWidth = wrap;
            _layout = OverlayTextLayout.Build(Text, new Typeface(OverlayFonts.Resolve(FontFamily), FontStyle, FontWeight, FontStretch),
                FontSize, wrap, LetterSpacing, BalanceLines, TextAlignment, VisualTreeHelper.GetDpi(this).PixelsPerDip, CultureInfo.CurrentUICulture);
            return _layout;
        }

        private Pen EnsureStrokePen()
        {
            if (_strokePen != null) return _strokePen;
            _strokePen = new Pen(StrokeBrush, StrokeThickness * 2)
            {
                DashCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                StartLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (_strokePen.CanFreeze) _strokePen.Freeze();
            return _strokePen;
        }

        private Pen EnsureShadowPen()
        {
            if (_shadowPen != null || ShadowBrush == null) return _shadowPen;
            // The drop covers the outlined shape and is a touch wider still, which softens its edge.
            _shadowPen = new Pen(ShadowBrush, StrokeThickness * 2 + Math.Max(0.5, ShadowOffset * 0.5))
            {
                EndLineCap = PenLineCap.Round,
                StartLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            if (_shadowPen.CanFreeze) _shadowPen.Freeze();
            return _shadowPen;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _constraintWidth = availableSize.Width;
            var layout = EnsureLayout();
            double strokePadding = StrokeThickness * 2;
            return new Size(
                Math.Min(layout.Width + strokePadding, availableSize.Width),
                Math.Min(layout.Height + strokePadding, availableSize.Height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var layout = EnsureLayout();
            if (layout.Geometry == null || layout.Geometry.IsEmpty()) return;

            // Centred and right-aligned text stays in place when the parent stretches the control wider than its text.
            double box = layout.Width + StrokeThickness * 2;
            double ox = StrokeThickness + (RenderSize.Width > box + 0.5
                ? TextAlignment switch
                {
                    TextAlignment.Center => (RenderSize.Width - box) / 2,
                    TextAlignment.Right => RenderSize.Width - box,
                    _ => 0,
                }
                : 0);
            double oy = StrokeThickness;

            if (ShadowBrush != null && ShadowOffset > 0)
            {
                drawingContext.PushTransform(new TranslateTransform(ox, oy + ShadowOffset));
                drawingContext.DrawGeometry(ShadowBrush, EnsureShadowPen(), layout.Geometry);
                drawingContext.Pop();
            }

            drawingContext.PushTransform(new TranslateTransform(ox, oy));
            if (StrokeThickness > 0 && StrokeBrush != null)
                drawingContext.DrawGeometry(null, EnsureStrokePen(), layout.Geometry);
            drawingContext.DrawGeometry(Foreground, null, layout.Geometry);
            drawingContext.Pop();
        }

        /// <summary>
        /// Measures the text at a specific font size without changing the control's state.
        /// Used by SubtitleLayoutEngine for auto-scaling calculations.
        /// </summary>
        public static System.Windows.Size MeasureText(
            string text, double fontSize, FontFamily fontFamily, FontWeight fontWeight,
            FontStyle fontStyle, double maxWidth, double strokeThickness, double pixelsPerDip)
            => MeasureText(text, fontSize, fontFamily, fontWeight, fontStyle, maxWidth, strokeThickness, pixelsPerDip, 0, false, out _);

        /// <summary>The same measurement with letter spacing and balanced lines; also reports the line count.</summary>
        public static System.Windows.Size MeasureText(
            string text, double fontSize, FontFamily fontFamily, FontWeight fontWeight,
            FontStyle fontStyle, double maxWidth, double strokeThickness, double pixelsPerDip,
            double letterSpacing, bool balanceLines, out int lines)
        {
            lines = 0;
            if (string.IsNullOrEmpty(text))
                return new System.Windows.Size(0, fontSize + strokeThickness * 2);

            double wrap = maxWidth > 0 && !double.IsPositiveInfinity(maxWidth) && maxWidth - strokeThickness * 2 > 0
                ? maxWidth - strokeThickness * 2
                : double.PositiveInfinity;
            var layout = OverlayTextLayout.Build(text,
                new Typeface(OverlayFonts.Resolve(fontFamily), fontStyle, fontWeight, FontStretches.Normal),
                fontSize, wrap, letterSpacing, balanceLines, TextAlignment.Center, pixelsPerDip, CultureInfo.CurrentUICulture);
            lines = layout.Lines;
            double strokePadding = strokeThickness * 2;
            return new System.Windows.Size(layout.Width + strokePadding, layout.Height + strokePadding);
        }
    }
}
