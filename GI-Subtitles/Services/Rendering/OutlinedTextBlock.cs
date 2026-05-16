using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// Custom WPF control that renders text with a solid stroke outline.
    /// Uses FormattedText.BuildGeometry() for crisp, GPU-friendly outlined text
    /// that remains readable against any background brightness.
    ///
    /// Designed as a drop-in replacement for TextBlock/TextBox in subtitle overlays.
    /// Future: can be reused by EmbeddedIllusionLayoutEngine for in-game text rendering.
    /// </summary>
    public class OutlinedTextBlock : FrameworkElement
    {
        #region Dependency Properties

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                new FontFamily("Segoe UI"),
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(16.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontWeights.Bold,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontStyles.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(FontStretches.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
            nameof(StrokeBrush), typeof(Brush), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(2.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
            nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(TextAlignment.Center,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
            nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(TextWrapping.Wrap,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        public static readonly DependencyProperty MaxTextWidthProperty = DependencyProperty.Register(
            nameof(MaxTextWidth), typeof(double), typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(double.PositiveInfinity,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnFormattedTextInvalidated));

        #endregion

        #region CLR Properties

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public FontStyle FontStyle
        {
            get => (FontStyle)GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public FontStretch FontStretch
        {
            get => (FontStretch)GetValue(FontStretchProperty);
            set => SetValue(FontStretchProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public Brush StrokeBrush
        {
            get => (Brush)GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)GetValue(TextWrappingProperty);
            set => SetValue(TextWrappingProperty, value);
        }

        public double MaxTextWidth
        {
            get => (double)GetValue(MaxTextWidthProperty);
            set => SetValue(MaxTextWidthProperty, value);
        }

        #endregion

        private FormattedText _formattedText;
        private Geometry _textGeometry;
        private Pen _strokePen;

        private static void OnFormattedTextInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (OutlinedTextBlock)d;
            control._formattedText = null;
            control._textGeometry = null;
            control._strokePen = null;
        }

        private FormattedText EnsureFormattedText()
        {
            if (_formattedText != null)
                return _formattedText;

            var text = Text ?? string.Empty;

            _formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                FontSize,
                Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            _formattedText.TextAlignment = TextAlignment;

            if (TextWrapping == TextWrapping.Wrap || TextWrapping == TextWrapping.WrapWithOverflow)
            {
                double maxWidth = MaxTextWidth;
                // Always clamp to actual available layout width — prevents TextAlignment.Center
                // from centering text within a box larger than the control, which shifts text right
                if (_constraintWidth > 0 && !double.IsPositiveInfinity(_constraintWidth))
                {
                    maxWidth = (double.IsPositiveInfinity(maxWidth) || maxWidth <= 0)
                        ? _constraintWidth
                        : Math.Min(maxWidth, _constraintWidth);
                }

                if (maxWidth > 0 && !double.IsPositiveInfinity(maxWidth))
                {
                    // Account for stroke extending beyond text bounds
                    double effectiveWidth = maxWidth - StrokeThickness * 2;
                    if (effectiveWidth > 0)
                        _formattedText.MaxTextWidth = effectiveWidth;
                }
            }

            return _formattedText;
        }

        private Geometry EnsureGeometry()
        {
            if (_textGeometry != null)
                return _textGeometry;

            var ft = EnsureFormattedText();
            _textGeometry = ft.BuildGeometry(new Point(StrokeThickness, StrokeThickness));

            return _textGeometry;
        }

        private Pen EnsureStrokePen()
        {
            if (_strokePen != null)
                return _strokePen;

            _strokePen = new Pen(StrokeBrush, StrokeThickness * 2)
            {
                DashCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                StartLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

            if (_strokePen.CanFreeze)
                _strokePen.Freeze();

            return _strokePen;
        }

        private double _constraintWidth;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            _constraintWidth = availableSize.Width;

            // Invalidate cached formatted text so it picks up the new constraint
            _formattedText = null;
            _textGeometry = null;

            var ft = EnsureFormattedText();

            double strokePadding = StrokeThickness * 2;
            double width = ft.Width + strokePadding;
            double height = ft.Height + strokePadding;

            return new System.Windows.Size(
                Math.Min(width, availableSize.Width),
                Math.Min(height, availableSize.Height));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var geometry = EnsureGeometry();
            var ft = EnsureFormattedText();
            var pen = EnsureStrokePen();

            // When TextAlignment is Center/Right and MaxTextWidth > actual text width,
            // FormattedText.BuildGeometry places glyphs centered/right within MaxTextWidth.
            // But MeasureOverride reports only actual content width, so the geometry
            // renders offset to the right. Compensate by shifting the drawing origin.
            double translateX = 0;
            if (ft.MaxTextWidth > 0 && ft.MaxTextWidth > ft.Width)
            {
                if (TextAlignment == TextAlignment.Center)
                    translateX = -(ft.MaxTextWidth - ft.Width) / 2;
                else if (TextAlignment == TextAlignment.Right)
                    translateX = -(ft.MaxTextWidth - ft.Width);
            }

            if (Math.Abs(translateX) > 0.5)
                drawingContext.PushTransform(new TranslateTransform(translateX, 0));

            // Draw stroke (outline) first — behind the fill
            if (StrokeThickness > 0 && StrokeBrush != null)
            {
                drawingContext.DrawGeometry(null, pen, geometry);
            }

            // Draw filled text on top
            drawingContext.DrawGeometry(Foreground, null, geometry);

            if (Math.Abs(translateX) > 0.5)
                drawingContext.Pop();
        }

        /// <summary>
        /// Measures the text at a specific font size without changing the control's state.
        /// Used by SubtitleLayoutEngine for auto-scaling calculations.
        /// </summary>
        public static System.Windows.Size MeasureText(
            string text, double fontSize, FontFamily fontFamily, FontWeight fontWeight,
            FontStyle fontStyle, double maxWidth, double strokeThickness, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text))
                return new System.Windows.Size(0, fontSize + strokeThickness * 2);

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily, fontStyle, fontWeight, FontStretches.Normal),
                fontSize,
                Brushes.White,
                pixelsPerDip);

            ft.TextAlignment = TextAlignment.Center;

            if (maxWidth > 0 && !double.IsPositiveInfinity(maxWidth))
            {
                double effectiveWidth = maxWidth - strokeThickness * 2;
                if (effectiveWidth > 0)
                    ft.MaxTextWidth = effectiveWidth;
            }

            double strokePadding = strokeThickness * 2;
            return new System.Windows.Size(ft.Width + strokePadding, ft.Height + strokePadding);
        }
    }
}
