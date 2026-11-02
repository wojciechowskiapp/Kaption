using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GI_Subtitles.Services.Characters;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Read-only character page; shared by the Ctrl+Q popup and the Characters tab. It scrolls itself so the
    /// section tabs can stay pinned once the hero has scrolled away.
    /// </summary>
    public partial class CharacterOverviewView : UserControl
    {
        private bool _surfaceKnown;

        public CharacterOverviewView()
        {
            InitializeComponent();
        }

        public CharacterOverviewView(CharacterOverviewModel model) : this()
        {
            if (model == null) return;
            var vm = new CharacterOverviewViewModel(model);
            DataContext = vm;
            if (vm.Sections.Count > 0) vm.SetActiveSection(vm.Sections[0].Key);
            // The hero, the summary and the build paint first; the long lists follow a frame later.
            Dispatcher.BeginInvoke(new Action(RevealLowerSections), DispatcherPriority.Background);
        }

        private CharacterOverviewViewModel Vm => DataContext as CharacterOverviewViewModel;

        public double VerticalOffset => Scroller.VerticalOffset;

        public void ScrollTo(double offset) => Scroller.ScrollToVerticalOffset(offset);

        // The host palette says whether the surface is dark; element text is tuned to it before the first paint.
        protected override Size MeasureOverride(Size constraint)
        {
            if (!_surfaceKnown && Vm != null)
            {
                _surfaceKnown = true;
                Vm.SetSurface(TryFindResource("Char_IsDark") is bool dark && dark);
            }
            return base.MeasureOverride(constraint);
        }

        private void RevealLowerSections()
        {
            if (LowerSections.Visibility != Visibility.Visible) LowerSections.Visibility = Visibility.Visible;
        }

        /// <summary>Scrolls so the section's heading sits under the pinned tabs.</summary>
        public bool ScrollToSection(string key)
        {
            if (LowerSections.Visibility != Visibility.Visible)
            {
                RevealLowerSections();
                UpdateLayout();
            }
            if (!(FindName("Section_" + key) is FrameworkElement target) || target.Visibility != Visibility.Visible) return false;
            try
            {
                double top = target.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
                double pinned = InlineTabs.ActualHeight;
                Scroller.ScrollToVerticalOffset(Math.Max(0, Scroller.VerticalOffset + top - pinned + 8));
            }
            catch (InvalidOperationException)
            {
                target.BringIntoView(); // not laid out yet
            }
            Vm?.SetActiveSection(key);
            return true;
        }

        private void SectionTab_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string key) ScrollToSection(key);
        }

        // The tab row scrolls sideways under the mouse wheel when it is wider than the page.
        private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ScrollViewer row) || row.ExtentWidth <= row.ViewportWidth) return;
            row.ScrollToHorizontalOffset(row.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // Pins the tabs once the inline row leaves the top and marks the section being read.
        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
            try
            {
                double inlineTop = InlineTabs.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
                PinnedTabs.Visibility = inlineTop < 0 && InlineTabs.Visibility == Visibility.Visible
                    ? Visibility.Visible : Visibility.Collapsed;

                var vm = Vm;
                if (vm == null) return;
                double line = InlineTabs.ActualHeight + 24;
                string active = vm.Sections.Count > 0 ? vm.Sections[0].Key : null;
                foreach (var s in vm.Sections)
                {
                    if (!(FindName("Section_" + s.Key) is FrameworkElement el) || !el.IsVisible) continue;
                    if (el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y <= line) active = s.Key;
                }
                // At the very bottom the last section is the one being read, however short it is.
                if (Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1 && vm.Sections.Count > 0)
                    active = vm.Sections[vm.Sections.Count - 1].Key;
                if (active != null) vm.SetActiveSection(active);
            }
            catch (InvalidOperationException)
            {
                // a section was not in the tree yet; the next scroll event catches up
            }
        }

        // Links come from game data verified at build time, and only https ever reaches the UI.
        private void Link_Click(object sender, RoutedEventArgs e)
        {
            string url = (sender as ButtonBase)?.Tag as string;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Could not open {uri.Host}: {ex.Message}");
            }
        }
    }
}
