using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GI_Subtitles.Services.Rendering;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// Wygląd napisów's miniature: the user's real screen (the game window when it is open, else the monitor holding the
    /// dialogue area) shrunk to 640 px wide, the game's art muted behind it, the saved dialogue area outlined where it is,
    /// and the overlay's own <see cref="SubtitleCard"/> exactly where and as large as the app draws it, times the same
    /// scale. Dragging the subtitle (or the arrow keys, Shift for bigger steps) moves it; <see cref="PositionChanged"/>
    /// reports the new offsets. The type is too small here to judge, so <see cref="SubtitleTrueSize"/> shows the same
    /// panel at 1:1 next to it.
    /// <para>Works without the Settings code-behind (the render tests load the page loose): everything comes from its
    /// properties.</para>
    /// </summary>
    public class SubtitlePreview : Border
    {
        public const double FrameWidth = 640;

        public static readonly DependencyProperty LookProperty = DependencyProperty.Register(nameof(Look), typeof(OverlayStyle),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata(OverlayStyle.Default, (d, _) => ((SubtitlePreview)d).ApplyLook()));
        public static readonly DependencyProperty ArtProperty = DependencyProperty.Register(nameof(Art), typeof(ImageSource),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata(null, (d, e) => ((SubtitlePreview)d)._art.Source = (ImageSource)e.NewValue));
        public static readonly DependencyProperty SpeakerProperty = DependencyProperty.Register(nameof(Speaker), typeof(string),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata("Paimon", (d, _) => ((SubtitlePreview)d).ApplySample()));
        public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(string),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata("Ej, poczekaj!", (d, _) => ((SubtitlePreview)d).ApplySample()));
        public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(LookScene),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata(LookScene.Fallback("Genshin"), (d, _) => ((SubtitlePreview)d).ApplyScene()));
        /// <summary>The vertical offset used when nothing is saved (the game's default).</summary>
        public static readonly DependencyProperty DefaultPadProperty = DependencyProperty.Register(nameof(DefaultPad), typeof(int),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata(-140, (d, _) => ((SubtitlePreview)d).ApplyLook()));
        /// <summary>The width the app lets the subtitle text take (MaxOverlayWidth and the dialogue area's width).</summary>
        public static readonly DependencyProperty TextWidthProperty = DependencyProperty.Register(nameof(TextWidth), typeof(double),
            typeof(SubtitlePreview), new FrameworkPropertyMetadata(900.0, (d, _) => ((SubtitlePreview)d).ApplyLook()));

        public OverlayStyle Look { get => (OverlayStyle)GetValue(LookProperty); set => SetValue(LookProperty, value); }
        public ImageSource Art { get => (ImageSource)GetValue(ArtProperty); set => SetValue(ArtProperty, value); }
        public string Speaker { get => (string)GetValue(SpeakerProperty); set => SetValue(SpeakerProperty, value); }
        public string Line { get => (string)GetValue(LineProperty); set => SetValue(LineProperty, value); }
        public LookScene Scene { get => (LookScene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
        public int DefaultPad { get => (int)GetValue(DefaultPadProperty); set => SetValue(DefaultPadProperty, value); }
        public double TextWidth { get => (double)GetValue(TextWidthProperty); set => SetValue(TextWidthProperty, value); }

        /// <summary>The user moved the subtitle: new vertical and horizontal offsets (DIPs, as Config "Pad" stores them).</summary>
        public event Action<int, int> PositionChanged;

        /// <summary>The overlay's panel, drawn at its real size and shrunk with the frame.</summary>
        public SubtitleCard Card { get; }
        /// <summary>Where the panel sits in the frame now (frame pixels), for tests and hit checks.</summary>
        public Rect PanelInFrame { get; private set; }
        /// <summary>Screen DIPs to frame pixels.</summary>
        public double FrameScale { get; private set; } = 1;

        private readonly Image _art = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
        private readonly Canvas _stage = new() { ClipToBounds = true };
        private readonly Border _area;
        private readonly TextBlock _label;
        private Point? _dragStart;
        private int _dragPadV, _dragPadH, _livePadV, _livePadH;

        public SubtitlePreview()
        {
            Width = FrameWidth;
            Height = 360;
            ClipToBounds = true;
            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.SizeAll;
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "HairlineBrush");
            Background = OverlayTokens.Brush(OverlayTokens.Get("Ink950", Color.FromRgb(0x0B, 0x0D, 0x17)));
            SnapsToDevicePixels = true;
            AutomationProperties.SetAutomationId(this, "SubtitlePreview");

            var root = new Grid();
            RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
            root.Children.Add(_art);
            // Darkened: the art stands in for the game without competing with the subtitle.
            root.Children.Add(new Border { Background = Scrim() });

            // The saved dialogue area: a faint outline with two short lines for the game's own text.
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (double fraction in new[] { 0.46, 0.3 })
                lines.Children.Add(new Border
                {
                    Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2.5, 0, 2.5),
                    Background = OverlayTokens.Brush(OverlayTokens.Get("OverlayPreviewTextLineColor", Color.FromArgb(0x73, 0xC9, 0xCE, 0xD8))),
                    Tag = fraction,
                });
            _area = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = OverlayTokens.Brush(OverlayTokens.PreviewBandLine),
                Background = OverlayTokens.Brush(OverlayTokens.PreviewBand),
                IsHitTestVisible = false,
                Child = lines,
            };
            _stage.Children.Add(_area);
            Card = new SubtitleCard { SmoothResize = false, BackdropLuminance = 0.15, IsHitTestVisible = false };
            _stage.Children.Add(Card);
            root.Children.Add(_stage);

            // Which screen this is: the monitor (name and resolution) or the open game window.
            _label = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "BandDetailBrush");
            var chip = new Border
            {
                Margin = new Thickness(8), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Background = OverlayTokens.Brush(Color.FromArgb(0xB3, 0x0B, 0x0D, 0x17)),
                Child = _label,
            };
            root.Children.Add(chip);

            var ring = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            ring.SetResourceReference(Border.BorderBrushProperty, "FocusRingBrush");
            root.Children.Add(ring);
            IsKeyboardFocusedChanged += (_, __) => ring.Visibility = IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
            Child = root;

            ApplyScene();
            ApplyLook();
            ApplySample();
        }

        private static Brush Scrim()
        {
            var ink = OverlayTokens.Get("Ink950", Color.FromRgb(0x0B, 0x0D, 0x17));
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x73, ink.R, ink.G, ink.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, ink.R, ink.G, ink.B), 0.7));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB3, ink.R, ink.G, ink.B), 1));
            brush.Freeze();
            return brush;
        }

        private void ApplyScene()
        {
            var scene = Scene ?? LookScene.Fallback("Genshin");
            double aspect = scene.Frame.Width > 0 ? scene.Frame.Height / scene.Frame.Width : 9.0 / 16;
            Height = Math.Round(Math.Max(160, Math.Min(480, FrameWidth * aspect)));
            _label.Text = scene.Label ?? string.Empty;
            ((FrameworkElement)_label.Parent).Visibility = string.IsNullOrEmpty(scene.Label) ? Visibility.Collapsed : Visibility.Visible;
            InvalidateArrange();
        }

        private void ApplyLook()
        {
            var look = Look ?? OverlayStyle.Default;
            Card.ApplyStyle(look);
            Card.SubtitleText.MaxTextWidth = TextWidth > 0 ? TextWidth : 900;
            Card.HeaderText.MaxTextWidth = Card.SubtitleText.MaxTextWidth;
            _livePadV = look.PadVertical ?? DefaultPad;
            _livePadH = look.PadHorizontal;
            InvalidateArrange();
        }

        private void ApplySample()
        {
            Card.HeaderText.Text = Speaker ?? string.Empty;
            Card.HeaderText.Visibility = string.IsNullOrEmpty(Speaker) ? Visibility.Collapsed : Visibility.Visible;
            Card.SubtitleText.Text = Line ?? string.Empty;
            AutomationProperties.SetName(this, Line ?? string.Empty);
            InvalidateArrange();
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            var size = base.ArrangeOverride(arrangeSize);
            Place(arrangeSize);
            return size;
        }

        /// <summary>The screen at one scale: frame width over the frame's width in DIPs.</summary>
        private void Place(Size frame)
        {
            var scene = Scene ?? LookScene.Fallback("Genshin");
            if (frame.Width <= 0 || scene.Frame.Width <= 0) return;
            double s = frame.Width / scene.Frame.Width;
            FrameScale = s;
            var look = Look ?? OverlayStyle.Default;

            var a = scene.Area;
            Canvas.SetLeft(_area, (a.X - scene.Frame.X) * s);
            Canvas.SetTop(_area, (a.Y - scene.Frame.Y) * s);
            _area.Width = Math.Max(0, a.Width * s);
            _area.Height = Math.Max(0, a.Height * s);
            if (_area.Child is StackPanel lines)
                foreach (Border line in lines.Children) line.Width = Math.Max(8, _area.Width * (double)line.Tag);

            // The panel at its real size, placed as the app places it, then shrunk by the same scale.
            Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var panel = Card.DesiredSize;
            double reference = SubtitleCard.ReferenceHeight(look) + OverlayStyle.WindowMargin * 2;
            var rect = LookGeometry.PanelRect(scene, _livePadV, _livePadH, panel, reference);
            Card.GrowsUpward = OverlayPlacement.GrowsUpward(a.Top, a.Height, _livePadV, reference);
            Card.RenderTransform = new ScaleTransform(s, s);
            Canvas.SetLeft(Card, (rect.X - scene.Frame.X) * s);
            Canvas.SetTop(Card, (rect.Y - scene.Frame.Y) * s);
            PanelInFrame = new Rect((rect.X - scene.Frame.X) * s, (rect.Y - scene.Frame.Y) * s, rect.Width * s, rect.Height * s);
        }

        // ── Moving the subtitle ───────────────────────────────────────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            _dragStart = e.GetPosition(this);
            _dragPadV = _livePadV;
            _dragPadH = _livePadH;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragStart.HasValue || !IsMouseCaptured) return;
            var now = e.GetPosition(this);
            double s = FrameScale > 0 ? FrameScale : 1;
            _livePadV = Clamp(_dragPadV + (int)Math.Round((now.Y - _dragStart.Value.Y) / s), OverlayStyle.MinPad, OverlayStyle.MaxPad);
            _livePadH = Clamp(_dragPadH + (int)Math.Round((now.X - _dragStart.Value.X) / s), OverlayStyle.MinPad, OverlayStyle.MaxPad);
            InvalidateArrange();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragStart.HasValue) return;
            _dragStart = null;
            ReleaseMouseCapture();
            if (_livePadV != _dragPadV || _livePadH != _dragPadH) PositionChanged?.Invoke(_livePadV, _livePadH);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _dragStart = null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 2;
            int dv = e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            int dh = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            if (dv == 0 && dh == 0) return;
            _livePadV = Clamp(_livePadV + dv, OverlayStyle.MinPad, OverlayStyle.MaxPad);
            _livePadH = Clamp(_livePadH + dh, OverlayStyle.MinPad, OverlayStyle.MaxPad);
            InvalidateArrange();
            PositionChanged?.Invoke(_livePadV, _livePadH);
            e.Handled = true;
        }

        private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
    }

    /// <summary>
    /// "Podgląd 1:1": the same panel at its true size over a strip of the game's art, for judging size, face and look
    /// (the miniature above is to scale and too small for that). The looks apply to both.
    /// </summary>
    public class SubtitleTrueSize : Border
    {
        public static readonly DependencyProperty LookProperty = DependencyProperty.Register(nameof(Look), typeof(OverlayStyle),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata(OverlayStyle.Default, (d, _) => ((SubtitleTrueSize)d).Apply()));
        public static readonly DependencyProperty ArtProperty = DependencyProperty.Register(nameof(Art), typeof(ImageSource),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata(null, (d, e) => ((SubtitleTrueSize)d)._art.ImageSource = (ImageSource)e.NewValue));
        public static readonly DependencyProperty SpeakerProperty = DependencyProperty.Register(nameof(Speaker), typeof(string),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata("Paimon", (d, _) => ((SubtitleTrueSize)d).Apply()));
        public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(string),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata("Ej, poczekaj!", (d, _) => ((SubtitleTrueSize)d).Apply()));
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata("1:1", (d, e) => ((SubtitleTrueSize)d)._label.Text = (string)e.NewValue));
        public static readonly DependencyProperty TextWidthProperty = DependencyProperty.Register(nameof(TextWidth), typeof(double),
            typeof(SubtitleTrueSize), new FrameworkPropertyMetadata(900.0, (d, _) => ((SubtitleTrueSize)d).Apply()));

        public OverlayStyle Look { get => (OverlayStyle)GetValue(LookProperty); set => SetValue(LookProperty, value); }
        public ImageSource Art { get => (ImageSource)GetValue(ArtProperty); set => SetValue(ArtProperty, value); }
        public string Speaker { get => (string)GetValue(SpeakerProperty); set => SetValue(SpeakerProperty, value); }
        public string Line { get => (string)GetValue(LineProperty); set => SetValue(LineProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public double TextWidth { get => (double)GetValue(TextWidthProperty); set => SetValue(TextWidthProperty, value); }

        public SubtitleCard Card { get; }
        private readonly ImageBrush _art = new() { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Bottom };
        private readonly TextBlock _label;

        public SubtitleTrueSize()
        {
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "HairlineBrush");
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            MinHeight = 120;
            var root = new Grid();
            root.Children.Add(new Border { Background = _art, CornerRadius = new CornerRadius(9) });
            var ink = OverlayTokens.Get("Ink950", Color.FromRgb(0x0B, 0x0D, 0x17));
            root.Children.Add(new Border { Background = OverlayTokens.Brush(Color.FromArgb(0x8C, ink.R, ink.G, ink.B)), CornerRadius = new CornerRadius(9) });
            Card = new SubtitleCard { SmoothResize = false, BackdropLuminance = 0.15, IsHitTestVisible = false, Margin = new Thickness(16, 34, 16, 18) };
            root.Children.Add(Card);
            _label = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Text = "1:1" };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "BandDetailBrush");
            root.Children.Add(new Border
            {
                Margin = new Thickness(8), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Background = OverlayTokens.Brush(Color.FromArgb(0xB3, ink.R, ink.G, ink.B)),
                Child = _label,
            });
            Child = root;
            AutomationProperties.SetAutomationId(this, "SubtitleTrueSize");
            Apply();
        }

        private void Apply()
        {
            Card.ApplyStyle(Look ?? OverlayStyle.Default);
            Card.SubtitleText.MaxTextWidth = TextWidth > 0 ? TextWidth : 900;
            Card.HeaderText.MaxTextWidth = Card.SubtitleText.MaxTextWidth;
            Card.HeaderText.Text = Speaker ?? string.Empty;
            Card.HeaderText.Visibility = string.IsNullOrEmpty(Speaker) ? Visibility.Collapsed : Visibility.Visible;
            Card.SubtitleText.Text = Line ?? string.Empty;
            AutomationProperties.SetName(this, (Label ?? string.Empty) + " " + (Line ?? string.Empty));
        }
    }
}
