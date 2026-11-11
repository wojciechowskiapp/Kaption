using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The Start page's stamina card. It only draws a <see cref="StaminaCardView"/>; the reading comes from the capture
    /// loop (MainWindow.Stamina.cs) through Config. The host handles the link (<see cref="OpenUrl"/>) and the two
    /// switches: <see cref="ReadChanged"/> is the player's consent to read the number off the game screen.
    /// </summary>
    public partial class StaminaCard : UserControl
    {
        private bool _applying;
        private string _timerUrl;

        /// <summary>The timer link was clicked.</summary>
        public event Action<string> OpenUrl;
        /// <summary>The player switched reading on (true) or off.</summary>
        public event Action<bool> ReadChanged;
        /// <summary>The player switched the "almost full" notice on or off.</summary>
        public event Action<bool> NoticeChanged;

        public StaminaCard()
        {
            InitializeComponent();
        }

        public StaminaCardView Current { get; private set; }

        /// <summary>Shows <paramref name="view"/>, or hides the card for a game the app cannot read.</summary>
        public void Apply(StaminaCardView view)
        {
            Current = view;
            if (view == null || !view.Visible)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            _applying = true;
            try
            {
                TitleRun.Text = view.PoolName;
                NumberRun.Text = view.Number == null ? string.Empty : "   " + view.Number;
                LineText.Text = view.Line;
                ReadToggle.IsChecked = view.On;
                AutomationProperties.SetName(ReadToggle, view.ToggleLabel ?? string.Empty);
                AutomationProperties.SetHelpText(ReadToggle, view.State == StaminaCardState.Off ? view.Line ?? string.Empty : string.Empty);
                ReadToggle.ToolTip = view.ToggleLabel;

                bool tracking = view.State == StaminaCardState.Tracking;
                FreshText.Text = view.Fresh ?? string.Empty;
                FreshRow.Visibility = tracking ? Visibility.Visible : Visibility.Collapsed;
                _timerUrl = view.TimerUrl;
                TimerButton.Visibility = string.IsNullOrEmpty(view.TimerUrl) ? Visibility.Collapsed : Visibility.Visible;

                NoticeToggle.Visibility = view.On ? Visibility.Visible : Visibility.Collapsed;
                NoticeToggle.IsChecked = view.NoticeOn;
                NoticeHint.Text = view.NoticeHint ?? string.Empty;
                Visibility = Visibility.Visible;
            }
            finally
            {
                _applying = false;
            }
        }

        // Apply sets IsChecked too: only the player's own flip is reported.
        private void ReadToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_applying) ReadChanged?.Invoke(ReadToggle.IsChecked == true);
        }

        private void NoticeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_applying) NoticeChanged?.Invoke(NoticeToggle.IsChecked == true);
        }

        private void Timer_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_timerUrl)) OpenUrl?.Invoke(_timerUrl);
        }
    }
}
