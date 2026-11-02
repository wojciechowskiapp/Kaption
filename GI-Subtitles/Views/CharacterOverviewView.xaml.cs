using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private string _activeKey;

        public CharacterOverviewView()
        {
            InitializeComponent();
            Hero.SizeChanged += (s, e) => FitTitleRow();
            Loaded += (s, e) => SyncSiteLanguage(); // the interface language may have changed while the page was hidden
        }

        public CharacterOverviewView(CharacterOverviewModel model) : this()
        {
            if (model == null) return;
            var vm = new CharacterOverviewViewModel(model);
            DataContext = vm;
            SyncSiteLanguage();
            if (vm.Sections.Count > 0) SetActive(vm.Sections[0].Key);
            // The hero, the summary and the build paint first; the long lists follow a frame later.
            Dispatcher.BeginInvoke(new Action(RevealLowerSections), DispatcherPriority.Background);
        }

        private CharacterOverviewViewModel Vm => DataContext as CharacterOverviewViewModel;

        public double VerticalOffset => Scroller.VerticalOffset;

        /// <summary>The Ctrl+Q popup hides the hero when its character strip heads the page.</summary>
        public bool ShowHero
        {
            get => Hero.Visibility == Visibility.Visible;
            set => Hero.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Restores a reading position (a host redraws the page once images arrive). The lower sections are laid
        /// out first when the offset needs them, or the offset would clamp to the top part's height.
        /// </summary>
        public void ScrollTo(double offset)
        {
            if (offset > Scroller.ScrollableHeight && LowerSections.Visibility != Visibility.Visible)
            {
                RevealLowerSections();
                UpdateLayout();
            }
            Scroller.ScrollToVerticalOffset(offset);
        }

        // The host palette says whether the surface is dark; element text is tuned to it before the first paint.
        protected override Size MeasureOverride(Size constraint)
        {
            if (!_surfaceKnown && Vm != null)
            {
                _surfaceKnown = true;
                Vm.SetSurface(TryFindResource("Char_IsDark") is bool dark && dark);
                SyncSiteLanguage();
            }
            return base.MeasureOverride(constraint);
        }

        // The page link follows the strings in use: Polish opens the Polish page, every other language the English one.
        private void SyncSiteLanguage() => Vm?.SetSiteLanguage(TryFindResource("Char_SiteLocale") as string);

        // The title row (name, chips, the kaption.one button) stays clear of the art's opaque part; when it does not fit
        // beside the title, the button wraps under it. Never narrower than the button or the title: wrap, never clip.
        private void FitTitleRow()
        {
            double art = TryFindResource("Char_ArtWidth") is double w ? w : 300;
            double gutters = TitleRow.Margin.Left + TitleRow.Margin.Right;
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            SitePageButton.Measure(unbounded);
            TitleBlock.Measure(unbounded);
            double widest = Math.Max(SitePageButton.DesiredSize.Width, TitleBlock.DesiredSize.Width);
            TitleRow.MaxWidth = Math.Max(widest, Hero.ActualWidth - gutters - art * 0.55);
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
            SetActive(key);
            return true;
        }

        private void SectionTab_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string key) ScrollToSection(key);
        }

        private void TabsBack_Click(object sender, RoutedEventArgs e) =>
            HorizontalOverflow.Page((sender as FrameworkElement)?.Tag as ScrollViewer, -1);

        private void TabsForward_Click(object sender, RoutedEventArgs e) =>
            HorizontalOverflow.Page((sender as FrameworkElement)?.Tag as ScrollViewer, +1);

        // Left / Right on a tab moves to the previous / next section, like the arrows of a tab strip.
        private void Tabs_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = Vm;
            if ((e.Key != Key.Left && e.Key != Key.Right) || vm == null || vm.Sections.Count == 0) return;
            int current = Math.Max(0, IndexOfActive(vm));
            int next = Math.Max(0, Math.Min(vm.Sections.Count - 1, current + (e.Key == Key.Right ? 1 : -1)));
            string key = vm.Sections[next].Key;
            ScrollToSection(key);
            if (TabFor(sender as ScrollViewer, key) is RadioButton tab) tab.Focus();
            e.Handled = true;
        }

        private static int IndexOfActive(CharacterOverviewViewModel vm)
        {
            for (int i = 0; i < vm.Sections.Count; i++) if (vm.Sections[i].IsActive) return i;
            return -1;
        }

        private static RadioButton TabFor(ScrollViewer row, string key)
        {
            if (row == null) return null;
            foreach (var tab in Descendants<RadioButton>(row))
                if (tab.Tag as string == key) return tab;
            return null;
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var deeper in Descendants<T>(child)) yield return deeper;
            }
        }

        /// <summary>Both tab rows (inline and pinned) scroll sideways to show the active tab whole.</summary>
        internal void KeepActiveTabInView()
        {
            var vm = Vm;
            int active = vm == null ? -1 : IndexOfActive(vm);
            if (active < 0) return;
            string key = vm.Sections[active].Key;
            foreach (var host in new FrameworkElement[] { InlineTabs, PinnedTabs })
            {
                if (host.Visibility != Visibility.Visible) continue;
                var row = Descendants<ScrollViewer>(host).FirstOrDefault(s => s.Name == "TabsRow");
                HorizontalOverflow.ScrollIntoView(row, TabFor(row, key));
            }
        }

        private void SetActive(string key)
        {
            var vm = Vm;
            if (vm == null || key == null || key == _activeKey) return;
            _activeKey = key;
            vm.SetActiveSection(key);
            // After the tab's IsChecked re-renders, so its position is final.
            Dispatcher.BeginInvoke(new Action(KeepActiveTabInView), DispatcherPriority.Loaded);
        }

        // Pins the tabs once the inline row leaves the top and marks the section being read.
        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
            try
            {
                double inlineTop = InlineTabs.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y;
                var pinned = inlineTop < 0 && InlineTabs.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
                if (PinnedTabs.Visibility != pinned)
                {
                    PinnedTabs.Visibility = pinned;
                    if (pinned == Visibility.Visible) Dispatcher.BeginInvoke(new Action(KeepActiveTabInView), DispatcherPriority.Loaded);
                }

                var vm = Vm;
                if (vm == null) return;
                double line = InlineTabs.ActualHeight + 24;
                string active = vm.Sections.Count > 0 ? vm.Sections[0].Key : null;
                foreach (var s in vm.Sections)
                {
                    if (!(FindName("Section_" + s.Key) is FrameworkElement el) || !IsShown(el)) continue;
                    if (el.TransformToAncestor(Scroller).Transform(new Point(0, 0)).Y <= line) active = s.Key;
                }
                // At the very bottom the last section is the one being read, however short it is.
                if (Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1 && vm.Sections.Count > 0)
                    active = vm.Sections[vm.Sections.Count - 1].Key;
                if (active != null) SetActive(active);
            }
            catch (InvalidOperationException)
            {
                // a section was not in the tree yet; the next scroll event catches up
            }
        }

        // Shown within the page (itself and every parent up to the scroller); unlike IsVisible, this holds for a
        // page laid out off screen too.
        private bool IsShown(FrameworkElement el)
        {
            for (DependencyObject d = el; d != null && !ReferenceEquals(d, Scroller); d = VisualTreeHelper.GetParent(d))
                if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            return true;
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

        private Action<string> _openCharacter;

        /// <summary>
        /// Lets "Gra też w drużynach" open the host's page: <paramref name="open"/> gets the character id. Only a host
        /// that can switch pages calls this (the Characters tab); elsewhere the rows are plain cards.
        /// </summary>
        public void EnableCharacterLinks(Action<string> open)
        {
            _openCharacter = open;
            if (Vm != null) Vm.CanOpenCharacters = open != null;
        }

        private void TeamHost_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as ButtonBase)?.Tag is string id && id.Length > 0) _openCharacter?.Invoke(id);
        }

        private void MoreTeamHosts_Click(object sender, RoutedEventArgs e)
        {
            Vm?.ShowAllTeamHosts();
            // Keyboard users carry on at the first host the button revealed.
            if (!IsKeyboardFocusWithin) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var buttons = Descendants<ButtonBase>(TeamHostsList).Where(b => b.IsVisible).ToList();
                if (buttons.Count > CharacterOverviewViewModel.TeamHostCap) buttons[CharacterOverviewViewModel.TeamHostCap].Focus();
            }), DispatcherPriority.Loaded);
        }
    }
}
