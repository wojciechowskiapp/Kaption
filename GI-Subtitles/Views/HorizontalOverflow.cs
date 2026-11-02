using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// A horizontal row that can be wider than its space (the character page's section tabs). Attached to its
    /// ScrollViewer it reports which sides can scroll (for the chevrons), fades only those edges, turns the wheel
    /// sideways and pans on drag or touch.
    /// </summary>
    public static class HorizontalOverflow
    {
        /// <summary>Width of the edge fade on a side that can scroll.</summary>
        public const double FadeWidth = 16;
        private const double DragThreshold = 6;

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(HorizontalOverflow), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

        private static readonly DependencyPropertyKey CanScrollLeftKey = DependencyProperty.RegisterAttachedReadOnly(
            "CanScrollLeft", typeof(bool), typeof(HorizontalOverflow), new PropertyMetadata(false));
        public static readonly DependencyProperty CanScrollLeftProperty = CanScrollLeftKey.DependencyProperty;
        public static bool GetCanScrollLeft(DependencyObject d) => (bool)d.GetValue(CanScrollLeftProperty);

        private static readonly DependencyPropertyKey CanScrollRightKey = DependencyProperty.RegisterAttachedReadOnly(
            "CanScrollRight", typeof(bool), typeof(HorizontalOverflow), new PropertyMetadata(false));
        public static readonly DependencyProperty CanScrollRightProperty = CanScrollRightKey.DependencyProperty;
        public static bool GetCanScrollRight(DependencyObject d) => (bool)d.GetValue(CanScrollRightProperty);

        private static readonly DependencyProperty DragProperty = DependencyProperty.RegisterAttached(
            "Drag", typeof(DragState), typeof(HorizontalOverflow), new PropertyMetadata(null));

        private sealed class DragState
        {
            public Point Start;
            public double Offset;
            public bool Moving;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ScrollViewer sv)) return;
            sv.ScrollChanged -= OnScrollChanged;
            sv.SizeChanged -= OnSizeChanged;
            sv.PreviewMouseWheel -= OnWheel;
            sv.PreviewMouseLeftButtonDown -= OnDown;
            sv.PreviewMouseMove -= OnMove;
            sv.PreviewMouseLeftButtonUp -= OnUp;
            sv.LostMouseCapture -= OnLostCapture;
            if (!(bool)e.NewValue) return;
            sv.ScrollChanged += OnScrollChanged;
            sv.SizeChanged += OnSizeChanged;
            sv.PreviewMouseWheel += OnWheel;
            sv.PreviewMouseLeftButtonDown += OnDown;
            sv.PreviewMouseMove += OnMove;
            sv.PreviewMouseLeftButtonUp += OnUp;
            sv.LostMouseCapture += OnLostCapture;
            sv.PanningMode = PanningMode.HorizontalOnly; // touch
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e) => Refresh((ScrollViewer)sender);
        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh((ScrollViewer)sender);

        /// <summary>Recomputes which sides can scroll and the edge fades. Layout calls it; tests may too.</summary>
        public static void Refresh(ScrollViewer sv)
        {
            if (sv == null) return;
            bool left = sv.HorizontalOffset > 0.5;
            bool right = sv.HorizontalOffset < sv.ScrollableWidth - 0.5;
            if (GetCanScrollLeft(sv) != left) sv.SetValue(CanScrollLeftKey, left);
            if (GetCanScrollRight(sv) != right) sv.SetValue(CanScrollRightKey, right);
            sv.OpacityMask = left || right ? EdgeFade(sv.ActualWidth, left, right) : null;
        }

        // An alpha mask, not a colour: opaque in the middle, fading out over FadeWidth on each side that scrolls.
        private static Brush EdgeFade(double width, bool left, bool right)
        {
            if (width <= FadeWidth * 2) return null;
            double f = FadeWidth / width;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(left ? Colors.Transparent : Colors.Black, 0));
            brush.GradientStops.Add(new GradientStop(Colors.Black, f));
            brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
            brush.GradientStops.Add(new GradientStop(right ? Colors.Transparent : Colors.Black, 1));
            brush.Freeze();
            return brush;
        }

        /// <summary>Scrolls the row so <paramref name="item"/> shows whole, clear of the edge fades.</summary>
        public static void ScrollIntoView(ScrollViewer sv, FrameworkElement item)
        {
            if (sv == null || item == null || !sv.IsAncestorOf(item) || sv.ScrollableWidth <= 0) return;
            double x;
            try { x = item.TransformToAncestor(sv).Transform(new Point(0, 0)).X; }
            catch (InvalidOperationException) { return; } // not in the tree yet
            double margin = FadeWidth + 8;
            double overLeft = margin - x;
            double overRight = x + item.ActualWidth + margin - sv.ViewportWidth;
            if (overLeft > 0) sv.ScrollToHorizontalOffset(sv.HorizontalOffset - overLeft);
            else if (overRight > 0) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + overRight);
        }

        /// <summary>One chevron press: most of a viewport towards <paramref name="direction"/> (-1 or +1).</summary>
        public static void Page(ScrollViewer sv, int direction)
        {
            if (sv == null) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset + Math.Sign(direction) * Math.Max(48, sv.ViewportWidth * 0.7));
        }

        // A row that overflows takes the wheel sideways; one that fits lets the page scroll.
        private static void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (sv.ScrollableWidth <= 0) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        private static void OnDown(object sender, MouseButtonEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (sv.ScrollableWidth <= 0) return;
            sv.SetValue(DragProperty, new DragState { Start = e.GetPosition(sv), Offset = sv.HorizontalOffset });
        }

        private static void OnMove(object sender, MouseEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (!(sv.GetValue(DragProperty) is DragState drag)) return;
            if (e.LeftButton != MouseButtonState.Pressed) { sv.ClearValue(DragProperty); return; }
            double dx = e.GetPosition(sv).X - drag.Start.X;
            if (!drag.Moving)
            {
                if (Math.Abs(dx) < DragThreshold) return;
                drag.Moving = true;
                sv.CaptureMouse(); // the tab under the pointer loses its press: a drag is never a click
            }
            sv.ScrollToHorizontalOffset(drag.Offset - dx);
            e.Handled = true;
        }

        private static void OnUp(object sender, MouseButtonEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (!(sv.GetValue(DragProperty) is DragState drag)) return;
            sv.ClearValue(DragProperty);
            if (!drag.Moving) return;
            sv.ReleaseMouseCapture();
            e.Handled = true;
        }

        // Only the row's own capture ends a drag; the tab losing its capture to the row (it bubbles here) does not.
        private static void OnLostCapture(object sender, MouseEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (ReferenceEquals(e.OriginalSource, sv) && sv.GetValue(DragProperty) is DragState drag && drag.Moving)
                sv.ClearValue(DragProperty);
        }
    }
}
