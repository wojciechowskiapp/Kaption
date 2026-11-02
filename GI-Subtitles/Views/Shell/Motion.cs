using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// The shell's micro-motion, only where it explains a change: the page that the rail opens rises in, the Start hero
    /// fades to a new state, a switch's knob slides to its side. 160 ms with an ease-out, and nothing at all when
    /// Windows animations are off or a high-contrast theme is on.
    /// </summary>
    public static class Motion
    {
        public const int DurationMs = 160;

        /// <summary>Tests force motion on or off; null follows Windows.</summary>
        internal static bool? Override { get; set; }

        public static bool Allowed => Override ?? (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);

        private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

        private static Duration Duration => new Duration(TimeSpan.FromMilliseconds(DurationMs));

        /// <summary>
        /// Brings <paramref name="element"/> in: from <paramref name="fromOpacity"/> to its own opacity, rising
        /// <paramref name="rise"/> px. The animations end on the element's own values, so nothing is held afterwards.
        /// </summary>
        public static void Enter(UIElement element, double rise = 8, double fromOpacity = 0)
        {
            if (element == null || !Allowed) return;
            double to = element.GetAnimationBaseValue(UIElement.OpacityProperty) is double own ? own : 1; // not a value mid-animation
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(fromOpacity, to, Duration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
            if (rise != 0 && Shift(element) is TranslateTransform shift)
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(rise, 0, Duration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }

        /// <summary>
        /// The knob has just jumped <paramref name="distance"/> px along x (a switch's template moves it at once): it starts
        /// where it was and slides to where it is.
        /// </summary>
        public static void SlideFrom(UIElement knob, double distance)
        {
            if (knob == null || !Allowed || distance == 0 || !(Shift(knob) is TranslateTransform shift)) return;
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-distance, 0, Duration) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });
        }

        // A translate transform of the element's own to animate; null when it already has another transform.
        private static TranslateTransform Shift(UIElement element)
        {
            if (element.RenderTransform is TranslateTransform own && !own.IsFrozen) return own;
            if (element.RenderTransform != null && element.RenderTransform != Transform.Identity) return null;
            var shift = new TranslateTransform();
            element.RenderTransform = shift;
            return shift;
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // ── the switch (ToggleSwitch style): shell:Motion.SlideKnob="True" ──

        public static readonly DependencyProperty SlideKnobProperty = DependencyProperty.RegisterAttached(
            "SlideKnob", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSlideKnobChanged));
        public static bool GetSlideKnob(DependencyObject d) => (bool)d.GetValue(SlideKnobProperty);
        public static void SetSlideKnob(DependencyObject d, bool value) => d.SetValue(SlideKnobProperty, value);

        private static void OnSlideKnobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is CheckBox box)) return;
            box.Checked -= OnToggled;
            box.Unchecked -= OnToggled;
            if (e.NewValue is true)
            {
                box.Checked += OnToggled;
                box.Unchecked += OnToggled;
            }
        }

        // Only a change the user makes slides (a click or Space); a value set from code or on load just sits there.
        private static void OnToggled(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box && box.IsLoaded && (box.IsMouseOver || box.IsKeyboardFocusWithin)) Toggled(box);
        }

        /// <summary>Slides the knob of a switch that has just changed (the knob part is named "knob").</summary>
        internal static void Toggled(CheckBox box)
        {
            if (!(box.Template?.FindName("knob", box) is FrameworkElement knob) || !(knob.Parent is FrameworkElement track)) return;
            // Left edge to right edge inside the track (the knob keeps the same inset on both sides).
            double travel = Math.Max(0, track.ActualWidth - knob.ActualWidth - 2 * Math.Max(knob.Margin.Left, knob.Margin.Right));
            SlideFrom(knob, box.IsChecked == true ? travel : -travel);
        }
    }
}
