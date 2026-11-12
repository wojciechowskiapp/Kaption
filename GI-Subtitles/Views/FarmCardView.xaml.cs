using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The farm card's content (FarmCardWindow hosts it over the game; the render tests host it alone). Raises one
    /// event per action and leaves what happens to the host.
    /// </summary>
    public partial class FarmCardView : UserControl
    {
        public event EventHandler CloseRequested;
        public event EventHandler DontShowRequested;
        public event EventHandler AddUidRequested;
        public event EventHandler FullListRequested;
        /// <summary>A line's link was clicked (an article, the stamina timer): the host opens it.</summary>
        public event Action<string> LinkRequested;
        /// <summary>Any click inside the card but closing: the host pins it.</summary>
        public event EventHandler Interacted;

        public FarmCardView()
        {
            InitializeComponent();
            ApplyAccent("Genshin");
        }

        public FarmCardViewModel ViewModel => (DataContext as TodayCardViewModel)?.Farm;

        /// <summary>The farm card alone, as before the other games had a card.</summary>
        public void Show(FarmCardViewModel vm) => Show(vm == null ? null : new TodayCardViewModel(vm));

        /// <summary>Any game's Today card. The farm parts (week strip, books, rows, tip) show only with Genshin's content.</summary>
        public void Show(TodayCardViewModel vm)
        {
            DataContext = vm;
            if (vm?.Eyebrow != null) EyebrowText.Text = vm.Eyebrow;
            var farm = vm?.HasFarm == true ? Visibility.Visible : Visibility.Collapsed;
            WeekStrip.Visibility = farm;
            WeekBlock.Visibility = farm;
            ResetChip.Visibility = vm == null || vm.HasReset ? Visibility.Visible : Visibility.Collapsed;
            TipLine.Visibility = string.IsNullOrEmpty(vm?.Tip) ? Visibility.Collapsed : Visibility.Visible;
        }

        private Color _accent = Colors.Goldenrod;
        private bool _dark = true;

        /// <summary>The game's accent (kaption.one) on the eyebrow, today's cell, the top talent and the glow.</summary>
        public void ApplyAccent(string game)
        {
            _accent = GameAccents.For(game);
            RefreshAccent();
        }

        /// <summary>Dark (over the game, the default) or light palette; the tier frames stay the game's own.</summary>
        public void UsePalette(bool dark)
        {
            _dark = dark;
            string wanted = dark ? "CharacterPaletteDark.xaml" : "CharacterPaletteLight.xaml";
            var merged = Resources.MergedDictionaries;
            var current = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("CharacterPalette") == true);
            if (current?.Source.OriginalString.EndsWith(wanted, StringComparison.Ordinal) != true)
            {
                var next = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{typeof(FarmCardView).Assembly.GetName().Name};component/Resources/{wanted}"),
                };
                if (current != null) merged[merged.IndexOf(current)] = next;
                else merged.Insert(0, next);
            }
            RefreshAccent();
        }

        // On paper the light palette's own deep amber (4.9:1 on white) stands in for the game's hue in text.
        private void RefreshAccent()
        {
            if (_dark)
            {
                var accent = new SolidColorBrush(_accent);
                accent.Freeze();
                Resources["Char_Accent"] = accent;
            }
            else
            {
                Resources.Remove("Char_Accent");
            }
            var glow = new RadialGradientBrush(_accent, Color.FromArgb(0, _accent.R, _accent.G, _accent.B));
            glow.Freeze();
            Glow.Fill = glow;
            Glow.Opacity = _dark ? 0.22 : 0.16;
        }

        /// <summary>0..1 of the reading time left; null hides the bar (pinned).</summary>
        public void SetCountdown(double? fraction)
        {
            if (fraction == null)
            {
                CountdownTrack.Visibility = Visibility.Collapsed;
                return;
            }
            CountdownTrack.Visibility = Visibility.Visible;
            CountdownBar.Width = Math.Max(0, CountdownTrack.ActualWidth * Math.Clamp(fraction.Value, 0, 1));
        }

        /// <summary>The arrival: header, then the tiles and rows one after another. Skipped when Windows turns animations off.</summary>
        public void PlayEntrance()
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            int i = 0;
            Rise(DayTitle, i++);
            Rise(WeekBlock, i++);
            foreach (var item in Containers(SectionList)) Rise(item, i++);
            foreach (var item in Containers(BookTiles)) Rise(item, i++);
            foreach (var item in Containers(RowList)) Rise(item, i++);
            Rise(PlayerNote, i++);
            Rise(TipLine, i);
        }

        private static System.Collections.Generic.IEnumerable<FrameworkElement> Containers(ItemsControl list)
        {
            for (int k = 0; k < list.Items.Count; k++)
                if (list.ItemContainerGenerator.ContainerFromIndex(k) is FrameworkElement fe) yield return fe;
        }

        private static void Rise(UIElement element, int index)
        {
            if (element == null || element.Visibility != Visibility.Visible) return;
            var delay = TimeSpan.FromMilliseconds(60 + index * 35);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var move = new TranslateTransform(0, 8);
            element.RenderTransform = move;
            element.Opacity = 0;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = ease });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void DontShowButton_Click(object sender, RoutedEventArgs e) => DontShowRequested?.Invoke(this, EventArgs.Empty);

        private void AddUidButton_Click(object sender, RoutedEventArgs e)
        {
            Interacted?.Invoke(this, EventArgs.Empty);
            AddUidRequested?.Invoke(this, EventArgs.Empty);
        }

        // A line that is a link as a whole (an article): the click pins the card and asks the host to open it.
        private void Line_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TodayCardViewModel.LineVm { IsLink: true } line) return;
            Interacted?.Invoke(this, EventArgs.Empty);
            LinkRequested?.Invoke(line.Url);
        }

        private void LineLink_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TodayCardViewModel.LineVm line || string.IsNullOrEmpty(line.Url)) return;
            Interacted?.Invoke(this, EventArgs.Empty);
            LinkRequested?.Invoke(line.Url);
        }

        private void FullListButton_Click(object sender, RoutedEventArgs e)
        {
            Interacted?.Invoke(this, EventArgs.Empty);
            FullListRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
