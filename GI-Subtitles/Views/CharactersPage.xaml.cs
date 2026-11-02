using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Services.Translation;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>Where the Characters page gets the translation pack and the active game (the Settings window).</summary>
    public interface ICharacterPageHost
    {
        OptimizedMatcher CharacterPack { get; }
        bool IsCharacterPackReady { get; }
        string CharacterGame { get; }
    }

    /// <summary>List row. Public so WPF binding can read it; IconPath updates in place once downloaded.</summary>
    public sealed class CharacterListItem : INotifyPropertyChanged
    {
        private string _iconPath;

        public string Id { get; set; } = "";
        /// <summary>Sort key: the English name, plus the Polish protagonist title when there is one.</summary>
        public string Display { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Polish protagonist title ("Podróżnik"), else empty. See <see cref="CharacterNames"/>.</summary>
        public string Gloss { get; set; } = "";
        public string Initial { get; set; } = "";
        public string Rarity { get; set; } = "";
        public string RarityTier { get; set; } = "";
        public Brush ElementFill { get; set; }
        public Brush ElementInk { get; set; }
        public Brush ElementSoft { get; set; }
        public Brush ElementWash { get; set; }
        internal string ElementKey { get; set; } = "";
        internal string IconUrl { get; set; }
        internal string SearchKey { get; set; } = "";

        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath == value) return;
                _iconPath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPath)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // Screen readers and UI Automation name a list row by its item's text.
        public override string ToString() => Name;
    }

    /// <summary>Element filter swatch. No selection means every element.</summary>
    public sealed class CharacterElementFilterItem
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public Brush Fill { get; set; }

        public override string ToString() => Label;
    }

    public partial class CharactersPage : UserControl
    {
        internal enum PageState { Loading, Ready, Empty, Error }

        private static readonly double[] SkeletonWidths = { 118, 84, 132, 96, 74, 124, 104, 90, 128, 82 };
        // A load that finishes this fast never shows the skeleton, so quick tab switches do not flicker.
        private static readonly TimeSpan SkeletonDelay = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan SelectionSettle = TimeSpan.FromMilliseconds(90);

        private CharacterCatalog _catalog = CharacterCatalog.Empty;
        private List<CharacterListItem> _items = new List<CharacterListItem>();
        private MatcherTranslationLookup _lookup;
        private string _loadedFor;
        private int _loadVersion;
        private int _selectionVersion;
        private string _shownId;
        private bool _hooked;
        // Resolved on the UI thread: lookups run on worker threads and must not touch the visual tree.
        private ICharacterPageHost _host;
        private object _translatedWith;
        private DispatcherTimer _packWatch;
        private DispatcherTimer _skeletonTimer;
        private Func<string, Task<CharacterCatalog>> _catalogSource = CharacterCatalogService.GetAsync;

        public CharactersPage()
        {
            InitializeComponent();
            ListSkeleton.ItemsSource = SkeletonWidths;
            // Subscribed for the page's life, not per Loaded: a tab page unloads whenever another tab is shown,
            // and a data update while it is hidden must still mark it stale.
            CharacterCatalogService.CatalogChanged += OnCatalogChanged;
            Loaded += (s, e) =>
            {
                _host = _host ?? Window.GetWindow(this) as ICharacterPageHost;
                if (_hooked || !(Window.GetWindow(this) is Window window)) return;
                _hooked = true;
                window.Closed += (ws, we) =>
                {
                    CharacterCatalogService.CatalogChanged -= OnCatalogChanged;
                    _packWatch?.Stop();
                    _skeletonTimer?.Stop();
                };
            };
        }

        /// <summary>Overrides the host lookups (tests, or a host that is not the Settings window).</summary>
        internal Func<OptimizedMatcher> PackSource { get; set; }
        internal Func<bool> PackReady { get; set; }
        internal Func<string> GameSource { get; set; }
        internal ITranslationLookup LookupOverride { get; set; }
        /// <summary>False keeps the page on images already on disk (render tests: no network, no stray work).</summary>
        internal bool FetchImages { get; set; } = true;
        internal PageState State { get; private set; } = PageState.Loading;

        private ICharacterPageHost Host => _host;
        private OptimizedMatcher Pack => PackSource != null ? PackSource() : Host?.CharacterPack;
        private bool IsPackReady => PackReady != null ? PackReady() : Host?.IsCharacterPackReady == true;
        private string CurrentGame() => GameSource?.Invoke() ?? Host?.CharacterGame ?? Config.Get("Game", "Genshin") ?? "Genshin";

        private ITranslationLookup Lookup() => LookupOverride ?? _lookup ?? (_lookup = new MatcherTranslationLookup(() => Pack));

        // ── loading ──────────────────────────────────────────────────────

        private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(e.NewValue is bool visible) || !visible) return;
            _host = _host ?? Window.GetWindow(this) as ICharacterPageHost;
            string game = CurrentGame();
            if (!string.Equals(_loadedFor, game, StringComparison.OrdinalIgnoreCase) ||
                NeedsRetranslation(_translatedWith, Pack, IsPackReady))
                _ = LoadAsync(game);
        }

        /// <summary>True when text was built without the now-ready pack (or with an older one).</summary>
        internal static bool NeedsRetranslation(object translatedWith, object current, bool currentReady) =>
            currentReady && !ReferenceEquals(translatedWith, current);

        // Raised from the gamedata sync's background thread.
        private void OnCatalogChanged(string game)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.Equals(game, CurrentGame(), StringComparison.OrdinalIgnoreCase)) return;
                _loadedFor = null;
                if (IsVisible) _ = LoadAsync(game);
            }));
        }

        // While the page shows English because the pack is still loading, check again once a second.
        private void WatchForPack()
        {
            if (_packWatch != null || IsPackReady) return;
            _packWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _packWatch.Tick += (s, e) =>
            {
                if (!IsVisible || !NeedsRetranslation(_translatedWith, Pack, IsPackReady)) return;
                _packWatch.Stop();
                _packWatch = null;
                _ = LoadAsync(CurrentGame());
            };
            _packWatch.Start();
        }

        /// <param name="catalogSource">Test seam; the app reads the installed file through CharacterCatalogService.</param>
        internal async Task LoadAsync(string game, Func<string, Task<CharacterCatalog>> catalogSource = null)
        {
            if (catalogSource != null) _catalogSource = catalogSource;
            int version = ++_loadVersion;
            _loadedFor = game;
            object translatedWith = IsPackReady ? Pack : null;
            string selectedId = (CharacterList.SelectedItem as CharacterListItem)?.Id ?? _shownId;
            string elementKey = (ElementFilter.SelectedItem as CharacterElementFilterItem)?.Key;
            if (_items.Count == 0) ShowStateSoon(PageState.Loading, version);
            try
            {
                var lookup = Lookup();
                var icons = CharacterImageCache.ForGame(game);
                var source = _catalogSource;
                var loaded = await Task.Run(async () =>
                {
                    var catalog = await source(game).ConfigureAwait(false);
                    var items = catalog.Characters
                        .Select(c => MakeItem(c, catalog, lookup, icons))
                        .OrderBy(i => i.Display, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                    return (catalog, items, filters: MakeFilters(catalog, lookup));
                });
                if (version != _loadVersion) return; // a newer load superseded this one

                _catalog = loaded.catalog;
                _items = loaded.items;
                _translatedWith = translatedWith;
                if (translatedWith == null) WatchForPack();
                if (loaded.catalog.IsEmpty)
                {
                    bool broken = CharacterCatalogService.StateOf(game) == CatalogLoadState.Unreadable;
                    ShowState(broken ? PageState.Error : PageState.Empty);
                    return;
                }

                OverviewHost.Content = null;
                _shownId = null;
                ElementFilter.ItemsSource = loaded.filters;
                ElementFilter.SelectedItem =
                    loaded.filters.FirstOrDefault(f => string.Equals(f.Key, elementKey, StringComparison.OrdinalIgnoreCase));
                ShowState(PageState.Ready);
                ApplyFilter();
                // Keep the open character after a refresh, else open the first one: the page is never blank.
                var shown = CharacterList.ItemsSource as IList<CharacterListItem>;
                var keep = shown?.FirstOrDefault(i => i.Id == selectedId) ?? shown?.FirstOrDefault();
                if (keep != null)
                {
                    CharacterList.SelectedItem = keep;
                    CharacterList.ScrollIntoView(keep);
                }

                if (FetchImages) _ = FetchListIconsAsync(loaded.items, icons, version);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Characters page load failed: {ex.Message}");
                if (version == _loadVersion) ShowState(PageState.Error);
            }
        }

        private void ShowStateSoon(PageState state, int version)
        {
            _skeletonTimer?.Stop();
            _skeletonTimer = new DispatcherTimer { Interval = SkeletonDelay };
            _skeletonTimer.Tick += (s, e) =>
            {
                _skeletonTimer.Stop();
                if (version == _loadVersion && _items.Count == 0) ShowState(state);
            };
            _skeletonTimer.Start();
        }

        internal void ShowState(PageState state)
        {
            State = state;
            if (state != PageState.Loading) _skeletonTimer?.Stop();
            bool browsing = state == PageState.Ready || state == PageState.Loading;
            Browser.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = state == PageState.Empty ? Visibility.Visible : Visibility.Collapsed;
            ErrorState.Visibility = state == PageState.Error ? Visibility.Visible : Visibility.Collapsed;
            SetSkeleton(ListSkeleton, state == PageState.Loading);
            SetSkeleton(DetailSkeleton, state == PageState.Loading || (state == PageState.Ready && OverviewHost.Content == null));
            CharacterList.Visibility = state == PageState.Loading ? Visibility.Hidden : Visibility.Visible;
            if (state == PageState.Loading) CountText.Text = "";
        }

        private void SetSkeleton(FrameworkElement skeleton, bool on)
        {
            if (on == (skeleton.Visibility == Visibility.Visible)) return;
            skeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var pulse = (Storyboard)FindResource("Pulse");
            if (on) pulse.Begin(skeleton, true);
            else pulse.Remove(skeleton);
        }

        private static CharacterListItem MakeItem(CharacterRecord c, CharacterCatalog catalog,
            ITranslationLookup lookup, CharacterImageCache icons)
        {
            string gloss = CharacterNames.Gloss(c.Name, e => lookup.TryTranslateExact(e, out string t) ? t : null);
            var hue = ElementPalette.For(catalog.Game, c.Element);
            string rarity = CharacterOverviewBuilder.RarityText(catalog.Game, c.Rarity);
            string url = catalog.IconUrl(IconKind.Character, c.Icon);
            return new CharacterListItem
            {
                Id = c.Id,
                Display = gloss.Length == 0 ? c.Name : $"{c.Name} ({gloss})",
                Name = c.Name,
                Gloss = gloss,
                Initial = CharacterOverviewViewModel.InitialOf(c.Name),
                // ZZZ ranks are letters; star games show "5★" rather than five stars in a narrow column.
                Rarity = rarity.Length == 1 && char.IsLetter(rarity[0]) ? rarity : c.Rarity > 0 ? c.Rarity + "★" : "",
                RarityTier = CharacterOverviewViewModel.Tier(catalog.Game, c.Rarity),
                ElementKey = c.Element ?? "",
                ElementFill = ElementPalette.Brush(hue),
                ElementInk = ElementPalette.Brush(ElementPalette.InkOn(hue)),
                ElementSoft = ElementPalette.Brush(hue, 0x40),
                ElementWash = ElementPalette.Brush(hue, 0x1F),
                IconUrl = url,
                IconPath = icons.CachedPathOrNull(url),
                SearchKey = FoldForSearch(c.Name + " " + gloss),
            };
        }

        private static List<CharacterElementFilterItem> MakeFilters(CharacterCatalog catalog, ITranslationLookup lookup) =>
            catalog.Characters
                .Select(c => c.Element)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ElementPalette.Order).ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
                .Select(e => new CharacterElementFilterItem
                {
                    Key = e,
                    Label = lookup.TryTranslateExact(e, out string t) && !string.IsNullOrWhiteSpace(t) ? t : e,
                    Fill = ElementPalette.Brush(ElementPalette.For(catalog.Game, e)),
                })
                .ToList();

        // Rows show what is on disk at once; the rest arrive in the background (the cache caps parallelism).
        private async Task FetchListIconsAsync(IReadOnlyList<CharacterListItem> items, CharacterImageCache icons, int version)
        {
            try
            {
                var missing = items.Where(i => i.IconPath == null && i.IconUrl != null).ToList();
                await Task.WhenAll(missing.Select(async item =>
                {
                    string path = await icons.GetAsync(item.IconUrl).ConfigureAwait(false);
                    if (path == null) return;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (version == _loadVersion) item.IconPath = path;
                    }));
                })).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Character icons: {ex.Message}");
            }
        }

        // ── filtering ────────────────────────────────────────────────────

        private void ApplyFilter()
        {
            string q = FoldForSearch(SearchBox.Text ?? "");
            string element = (ElementFilter.SelectedItem as CharacterElementFilterItem)?.Key;
            FilterClear.Visibility = element == null ? Visibility.Collapsed : Visibility.Visible;
            var shown = _items.Where(i =>
                (q.Length == 0 || i.SearchKey.Contains(q)) &&
                (element == null || string.Equals(i.ElementKey, element, StringComparison.OrdinalIgnoreCase))).ToList();
            CharacterList.ItemsSource = shown;
            NoResults.Visibility = shown.Count == 0 && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shown.Count == 0 && OverviewHost.Content == null) SetSkeleton(DetailSkeleton, false); // nothing will load
            CountText.Text = shown.Count == _items.Count
                ? string.Format(CultureInfo.CurrentCulture, L("Char_Count_Format", "{0} characters"), _items.Count)
                : string.Format(CultureInfo.CurrentCulture, L("Char_CountFiltered_Format", "{0} of {1}"), shown.Count, _items.Count);
            // Keep the highlight on the character still on screen; it is not rebuilt (same id).
            var current = shown.FirstOrDefault(i => i.Id == _shownId);
            if (current != null) CharacterList.SelectedItem = current;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool empty = string.IsNullOrEmpty(SearchBox.Text);
            SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilter();
        }

        private void ElementFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

        // A second click on the active swatch turns the filter off.
        private void ElementFilter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != ElementFilter; d = VisualTreeHelper.GetParent(d))
            {
                if (!(d is ListBoxItem item)) continue;
                if (item.IsSelected)
                {
                    ElementFilter.SelectedItem = null;
                    e.Handled = true;
                }
                return;
            }
        }

        private void FilterClear_Click(object sender, RoutedEventArgs e) => ElementFilter.SelectedItem = null;

        private void SearchClear_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            ElementFilter.SelectedItem = null;
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            CharacterCatalogService.Invalidate(CurrentGame()); // drops the cached result; CatalogChanged reloads
            _loadedFor = null;
            _ = LoadAsync(CurrentGame());
        }

        // ── keyboard ─────────────────────────────────────────────────────

        // Ctrl+F searches from anywhere on the page; Esc in the detail returns to the list.
        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && OverviewHost.IsKeyboardFocusWithin)
            {
                FocusList();
                e.Handled = true;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                case Key.Enter:
                    FocusList();
                    e.Handled = true;
                    break;
                case Key.Escape when SearchBox.Text.Length > 0:
                    SearchBox.Clear();
                    e.Handled = true;
                    break;
            }
        }

        // Enter or Right opens the page: focus moves to the first section link.
        private void CharacterList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Right) return;
            if (OverviewHost.Content is UIElement view &&
                view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                e.Handled = true;
        }

        private void FocusList()
        {
            if (CharacterList.Items.Count == 0) return;
            if (CharacterList.SelectedItem == null) CharacterList.SelectedIndex = 0;
            CharacterList.ScrollIntoView(CharacterList.SelectedItem);
            CharacterList.UpdateLayout();
            if (CharacterList.ItemContainerGenerator.ContainerFromItem(CharacterList.SelectedItem) is ListBoxItem row) row.Focus();
            else CharacterList.Focus();
        }

        // ── detail ───────────────────────────────────────────────────────

        private async void CharacterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (!(CharacterList.SelectedItem is CharacterListItem item) || item.Id == _shownId) return;
                var catalog = _catalog;
                var record = catalog.Find(item.Id);
                if (record == null) return;

                int version = ++_selectionVersion;
                // Arrow keys move through the list faster than a page lays out: build only where the user stops.
                if (_shownId != null) await Task.Delay(SelectionSettle);
                if (version != _selectionVersion) return;
                var lookup = Lookup();
                var icons = CharacterImageCache.ForGame(CurrentGame());
                // Build, read cached images and decode them off the UI thread; the view then only lays out.
                var model = await Task.Run(() =>
                {
                    var m = CharacterOverviewBuilder.Build(record, catalog, lookup);
                    CharacterIcons.ApplyCached(m, icons);
                    CharacterOverviewViewModel.PrepareVisuals(m);
                    return m;
                });
                if (version != _selectionVersion || model == null) return;

                _shownId = item.Id;
                OverviewHost.Content = new CharacterOverviewView(model);
                SetSkeleton(DetailSkeleton, false);

                // Missing images download in the background; re-render once they are on disk.
                if (!FetchImages || !await CharacterIcons.FetchMissingAsync(model, icons) || version != _selectionVersion) return;
                await Task.Run(() => CharacterOverviewViewModel.PrepareVisuals(model));
                if (version != _selectionVersion) return;
                double offset = (OverviewHost.Content as CharacterOverviewView)?.VerticalOffset ?? 0;
                var view = new CharacterOverviewView(model);
                OverviewHost.Content = view;
                _ = Dispatcher.BeginInvoke(new Action(() => view.ScrollTo(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Character page failed: {ex.Message}");
            }
        }

        /// <summary>Lower-case, no diacritics ("Łowca" → "lowca"), whitespace kept.</summary>
        internal static string FoldForSearch(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string decomposed = s.Trim().ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char ch in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private string L(string key, string fallback) => TryFindResource(key) as string ?? fallback;
    }
}
