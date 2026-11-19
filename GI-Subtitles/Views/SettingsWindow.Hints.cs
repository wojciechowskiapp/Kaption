using System;
using System.Windows;
using GI_Subtitles.Services.Hints;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The in-game hints switch at the top of the Shortcuts tab: on or off, and "Show it now". The card itself lives
    /// with the overlay (MainWindow.Hints.cs); this file only writes the switch and asks for the sheet.
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>Shows the sheet of shortcuts now, whatever is in front (MainWindow).</summary>
        public Action OnShowHintSheet;
        /// <summary>The shortcuts were registered again: what the card says about them is stale (MainWindow).</summary>
        public Action OnHotkeysRegistered;

        private bool _syncingHints, _hintsSwitchWatched;

        private void HintsToggle_Loaded(object sender, RoutedEventArgs e)
        {
            SyncHintsToggle();
            if (_hintsSwitchWatched) return;
            // "Don't show again" on the card switches hints off while this page may be open.
            _hintsSwitchWatched = true;
            HintSettings.Changed += OnHintSwitchChanged;
            Closed += (_, _) => HintSettings.Changed -= OnHintSwitchChanged;
        }

        private void OnHintSwitchChanged()
        {
            if (Dispatcher.CheckAccess()) SyncHintsToggle();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(SyncHintsToggle));
        }

        private void SyncHintsToggle()
        {
            if (HintsToggle == null) return;
            _syncingHints = true;
            try { HintsToggle.IsChecked = HintSettings.Enabled; }
            finally { _syncingHints = false; }
        }

        private void HintsToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingHints || !IsInitialized) return;
            HintSettings.Enabled = HintsToggle.IsChecked == true;
        }

        private void HintsShowNow_Click(object sender, RoutedEventArgs e) => OnShowHintSheet?.Invoke();
    }
}
