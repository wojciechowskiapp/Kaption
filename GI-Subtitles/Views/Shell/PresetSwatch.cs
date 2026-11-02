using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Rendering;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// A look card's sample: the overlay's own panel in that look over a bright scene, every card at the same scale, so
    /// the three show their difference (size, outline, panel, spacing) instead of describing it.
    /// </summary>
    public class PresetSwatch : Border
    {
        /// <summary>One scale for every card, so Kino's larger letters stay larger.</summary>
        public const double SampleScale = 0.46;

        public static readonly DependencyProperty PresetProperty = DependencyProperty.Register(nameof(Preset), typeof(OverlayPreset),
            typeof(PresetSwatch), new FrameworkPropertyMetadata(OverlayPreset.Classic, (d, _) => ((PresetSwatch)d).Apply()));
        public static readonly DependencyProperty SampleProperty = DependencyProperty.Register(nameof(Sample), typeof(string),
            typeof(PresetSwatch), new FrameworkPropertyMetadata("Ej, poczekaj!", (d, _) => ((PresetSwatch)d).Apply()));
        /// <summary>The face the overlay uses now, so the cards show it too.</summary>
        public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(nameof(Face), typeof(string),
            typeof(PresetSwatch), new FrameworkPropertyMetadata(SubtitleOverlayDefaults.FontFamily, (d, _) => ((PresetSwatch)d).Apply()));

        public OverlayPreset Preset { get => (OverlayPreset)GetValue(PresetProperty); set => SetValue(PresetProperty, value); }
        public string Sample { get => (string)GetValue(SampleProperty); set => SetValue(SampleProperty, value); }
        public string Face { get => (string)GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

        private readonly SubtitleCard _card = new()
        {
            SmoothResize = false,
            BackdropLuminance = 0,
            IsHitTestVisible = false,
            LayoutTransform = new ScaleTransform(SampleScale, SampleScale),
        };

        public PresetSwatch()
        {
            Height = 64;
            CornerRadius = new CornerRadius(8);
            ClipToBounds = true;
            SnapsToDevicePixels = true;
            var scene = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            scene.GradientStops.Add(new GradientStop(OverlayTokens.Get("OverlaySwatchSkyColor", Color.FromRgb(0x9D, 0xB7, 0xD0)), 0));
            scene.GradientStops.Add(new GradientStop(OverlayTokens.Get("OverlaySwatchGroundColor", Color.FromRgb(0xE4, 0xD8, 0xC2)), 0.85));
            scene.Freeze();
            Background = scene;
            _card.HorizontalAlignment = HorizontalAlignment.Center;
            _card.VerticalAlignment = VerticalAlignment.Center;
            _card.HeaderText.Visibility = Visibility.Collapsed;
            Child = _card;
            Apply();
        }

        private void Apply()
        {
            _card.ApplyStyle(OverlayStyle.PresetValues(Preset) with { FontFamily = Face ?? SubtitleOverlayDefaults.FontFamily });
            _card.SubtitleText.Text = Sample ?? string.Empty;
        }
    }
}
