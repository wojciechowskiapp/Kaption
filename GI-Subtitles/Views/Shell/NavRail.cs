using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GI_Subtitles.Views.Shell
{
    /// <summary>
    /// A TabControl shown as a left navigation rail (style RailTabControl in Themes/Shell.xaml): per-item icon, compact
    /// icon-only mode under a width, and Up/Down/Home/End/Enter keyboard handling. Tab indices and Tags stay as they
    /// are, so code that selects tabs keeps working.
    /// </summary>
    public static class NavRail
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(Geometry), typeof(NavRail), new PropertyMetadata(null));
        public static Geometry GetIcon(DependencyObject d) => (Geometry)d.GetValue(IconProperty);
        public static void SetIcon(DependencyObject d, Geometry value) => d.SetValue(IconProperty, value);

        /// <summary>Inherited by the items: true while the rail shows icons only.</summary>
        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
            "IsCompact", typeof(bool), typeof(NavRail),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
        public static bool GetIsCompact(DependencyObject d) => (bool)d.GetValue(IsCompactProperty);
        public static void SetIsCompact(DependencyObject d, bool value) => d.SetValue(IsCompactProperty, value);

        /// <summary>
        /// Inherited by the items: the active item's indicator colour (the current game's accent, set on the TabControl as
        /// a resource reference so a palette switch follows). Unset, the indicator keeps the app accent.
        /// </summary>
        public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
            "Accent", typeof(Brush), typeof(NavRail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnAccentChanged));
        public static Brush GetAccent(DependencyObject d) => (Brush)d.GetValue(AccentProperty);
        public static void SetAccent(DependencyObject d, Brush value) => d.SetValue(AccentProperty, value);

        /// <summary>Inherited: true where <see cref="AccentProperty"/> has a brush (the template's trigger condition).</summary>
        public static readonly DependencyProperty HasAccentProperty = DependencyProperty.RegisterAttached(
            "HasAccent", typeof(bool), typeof(NavRail), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
        public static bool GetHasAccent(DependencyObject d) => (bool)d.GetValue(HasAccentProperty);

        private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // Set once where the accent is set (the TabControl); the items inherit both.
            if (d is TabControl) d.SetValue(HasAccentProperty, e.NewValue != null);
        }

        /// <summary>The palette key of a game's rail indicator (the game's accent, deepened on the light palette).</summary>
        public static string AccentKeyFor(string game) => AmbienceArt.ForGame(game) switch
        {
            "starrail" => "RailIndicatorStarRailBrush",
            "zzz" => "RailIndicatorZzzBrush",
            _ => "RailIndicatorGenshinBrush",
        };

        /// <summary>Points the rail's indicator at the game's accent; follows a palette switch.</summary>
        public static void UseGameAccent(TabControl rail, string game) => rail?.SetResourceReference(AccentProperty, AccentKeyFor(game));

        /// <summary>Window width under which the rail collapses to icons; set on the TabControl to enable the rail.</summary>
        public static readonly DependencyProperty CompactBelowProperty = DependencyProperty.RegisterAttached(
            "CompactBelow", typeof(double), typeof(NavRail), new PropertyMetadata(double.NaN, OnCompactBelowChanged));
        public static double GetCompactBelow(DependencyObject d) => (double)d.GetValue(CompactBelowProperty);
        public static void SetCompactBelow(DependencyObject d, double value) => d.SetValue(CompactBelowProperty, value);

        public const double ExpandedWidth = 220, CompactWidth = 64;

        public static bool ShouldBeCompact(double windowWidth, double threshold) =>
            !double.IsNaN(threshold) && windowWidth > 0 && windowWidth < threshold;

        private static void OnCompactBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TabControl tabs) return;
            tabs.PreviewKeyDown -= OnRailKeyDown;
            tabs.PreviewKeyDown += OnRailKeyDown;
            tabs.Loaded -= OnRailLoaded;
            tabs.Loaded += OnRailLoaded;
            tabs.SelectionChanged -= OnPageChanged;
            tabs.SelectionChanged += OnPageChanged;
            Update(tabs);
        }

        // Selection changes from inside a page (a list, a combo box) bubble up here too; only the rail's own count.
        private static void OnPageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, sender) && sender is TabControl tabs && tabs.IsLoaded) PageEntered(tabs);
        }

        /// <summary>The page the rail just opened rises in (Motion: 160 ms, none with animations off).</summary>
        internal static void PageEntered(TabControl tabs)
        {
            if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is UIElement page) Motion.Enter(page, rise: 8);
        }

        private static void OnRailLoaded(object sender, RoutedEventArgs e)
        {
            var tabs = (TabControl)sender;
            var window = Window.GetWindow(tabs);
            if (window != null)
            {
                window.SizeChanged -= Handler;
                window.SizeChanged += Handler;
            }
            Update(tabs);
            void Handler(object s, SizeChangedEventArgs a) => Update(tabs);
        }

        private static void Update(TabControl tabs)
        {
            double width = Window.GetWindow(tabs)?.ActualWidth ?? tabs.ActualWidth;
            SetIsCompact(tabs, ShouldBeCompact(width, GetCompactBelow(tabs)));
        }

        private static void OnRailKeyDown(object sender, KeyEventArgs e)
        {
            var tabs = (TabControl)sender;
            if (!(e.OriginalSource is TabItem current) || !tabs.Items.Contains(current)) return;
            var items = tabs.Items.OfType<TabItem>().Where(t => t.IsEnabled && t.Visibility == Visibility.Visible).ToList();
            int i = items.IndexOf(current);
            if (i < 0 || items.Count == 0) return;
            TabItem target = e.Key switch
            {
                Key.Down => items[(i + 1) % items.Count],
                Key.Up => items[(i - 1 + items.Count) % items.Count],
                Key.Home => items[0],
                Key.End => items[^1],
                _ => null,
            };
            if (target != null)
            {
                target.IsSelected = true;
                target.Focus();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Enter or Key.Right)
            {
                // Into the page: the first focusable control of the selected content.
                current.IsSelected = true;
                tabs.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var presenter = FindDescendant<ContentPresenter>(tabs, p => p.Name == "PART_SelectedContentHost");
                    (presenter as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }), System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
            }
        }

        private static T FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && match(t)) return t;
                var deeper = FindDescendant(child, match);
                if (deeper != null) return deeper;
            }
            return null;
        }
    }

    /// <summary>"_Start" → "Start": a header with an access-key marker as plain text (tooltips, automation names).</summary>
    public sealed class AccessKeyTextConverter : IValueConverter
    {
        public static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf('_');
            while (i >= 0 && i + 1 < s.Length && s[i + 1] == '_') i = s.IndexOf('_', i + 2);
            return i < 0 ? s.Replace("__", "_") : (s.Remove(i, 1)).Replace("__", "_");
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Strip(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
