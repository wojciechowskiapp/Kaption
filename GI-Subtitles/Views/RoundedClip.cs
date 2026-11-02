using System.Windows;
using System.Windows.Media;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Clips an element to a rounded rectangle of its own size (Border.CornerRadius does not clip children).
    /// The geometry is rebuilt only when the size changes.
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement fe)) return;
            fe.SizeChanged -= OnSizeChanged;
            if ((double)e.NewValue > 0)
            {
                fe.SizeChanged += OnSizeChanged;
                Apply(fe);
            }
            else
            {
                fe.ClearValue(UIElement.ClipProperty);
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

        private static void Apply(FrameworkElement fe)
        {
            double r = GetRadius(fe);
            var clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
            clip.Freeze();
            fe.Clip = clip;
        }
    }
}
