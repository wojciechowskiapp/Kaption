using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using GI_Subtitles.Services.Today;
using System.Windows.Controls;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Start › today in the selected game: the Today card's model as a compact block. It only lays out what
    /// <see cref="TodayCardViewModel.BlockSections"/> holds; without a section it stays collapsed.
    /// </summary>
    public partial class TodayBlock : UserControl
    {
        /// <summary>A line's link (an article on the site).</summary>
        public event Action<string> OpenUrl;
        /// <summary>"Show the card": the same card the game start shows.</summary>
        public event Action ShowCardRequested;

        /// <summary>The player picked another server (its id); the host saves it and builds the block again.</summary>
        public event Action<string> ServerChanged;

        private bool _filling;

        public TodayBlock()
        {
            InitializeComponent();
        }

        /// <summary>The servers of the game's reset data and the chosen one; fewer than two hides the picker.</summary>
        public void SetServers(IReadOnlyList<TodayServerChoice> servers, string selectedId)
        {
            _filling = true;
            try
            {
                bool choice = servers != null && servers.Count > 1 && Visibility == Visibility.Visible;
                ServerRow.Visibility = choice ? Visibility.Visible : Visibility.Collapsed;
                ServerBox.ItemsSource = choice ? servers : null;
                if (choice)
                    ServerBox.SelectedItem = servers.FirstOrDefault(s => string.Equals(s.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? servers[0];
            }
            finally
            {
                _filling = false;
            }
        }

        private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || ServerBox.SelectedItem is not TodayServerChoice chosen) return;
            ServerChanged?.Invoke(chosen.Id);
        }

        /// <summary>Null, or a model without block sections, hides the block.</summary>
        public void Apply(TodayCardViewModel vm, string title)
        {
            if (vm == null || vm.BlockSections.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                SectionList.ItemsSource = null;
                return;
            }
            TitleRun.Text = title ?? "";
            DayRun.Text = string.IsNullOrEmpty(vm.DayTitle) ? "" : "  ·  " + vm.DayTitle;
            SectionList.ItemsSource = vm.BlockSections;
            Visibility = Visibility.Visible;
        }

        private void Link_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TodayCardViewModel.LineVm { Url: { Length: > 0 } url }) OpenUrl?.Invoke(url);
        }

        private void Show_Click(object sender, RoutedEventArgs e) => ShowCardRequested?.Invoke();
    }
}
