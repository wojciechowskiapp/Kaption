using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Settings, "In-game helper": what Kaption may read off the game screen besides the dialogue. Every reader of the
    /// selected game with one sentence and its own switch (off until switched on), what has been read, and one button
    /// that forgets it. The Diagnostics switch for sample pictures lives on the Help page. The words and the rows are
    /// Services/Assist/AssistCardModel.cs; the reading itself is the screen watch's (MainWindow.Assist.cs).
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _syncingAssist;
        /// <summary>The game whose last "delete what was read" left a file behind; the card says so until it works.</summary>
        private string _assistDeleteFailedFor;

        private void InitializeAssistCard()
        {
            if (AssistCard == null) return;
            AssistSettings.Changed += RefreshAssistCard;
            AssistStore.Default.Changed += OnAssistStoreChanged;
            UiLanguage.Changed += OnAssistLanguageChanged;
            IsVisibleChanged += (_, __) => { if (IsVisible) ApplyAssistCard(); };
            Closed += (_, __) =>
            {
                AssistSettings.Changed -= RefreshAssistCard;
                AssistStore.Default.Changed -= OnAssistStoreChanged;
                UiLanguage.Changed -= OnAssistLanguageChanged;
            };
            ApplyAssistCard();
        }

        private void OnAssistStoreChanged(string gameId) => RefreshAssistCard();

        private void OnAssistLanguageChanged(object sender, EventArgs e) => RefreshAssistCard();

        /// <summary>Draws the card again: after a switch, a new reading, a game or a language change. Safe from any thread.</summary>
        public void RefreshAssistCard()
        {
            if (Dispatcher.HasShutdownStarted) return;
            // BeginInvoke, not Invoke: it never throws once the dispatcher has shut down.
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(ApplyAssistCard));
            else ApplyAssistCard();
        }

        private void ApplyAssistCard()
        {
            if (AssistCard == null || !IsVisible) return; // nobody sees it; showing the window draws it again
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            AssistCardModel model = AssistCardModel.Build(game, AssistSettings.IsOn,
                game == null ? null : AssistStore.Default.Readings(game.GameId), L, CultureInfo.CurrentUICulture);

            _syncingAssist = true;
            try
            {
                ApplyAssistCardTo(FindName, model, L, AssistReaderToggle_Changed);
                if (AssistSamplesCheckBox != null) AssistSamplesCheckBox.IsChecked = AssistSettings.SaveSamples;
                if (AssistDeleteNote != null)
                {
                    bool failed = game != null && game.Id == _assistDeleteFailedFor;
                    AssistDeleteNote.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
                    if (failed && AssistDeleteButton != null) AssistDeleteButton.IsEnabled = true; // another try
                }
            }
            finally
            {
                _syncingAssist = false;
            }
        }

        /// <summary>Fills the card from a model (the render tests call this on the page without its code-behind).</summary>
        internal static void ApplyAssistCardTo(Func<string, object> find, AssistCardModel model, Func<string, string, string> l,
            RoutedEventHandler onToggle)
        {
            if (!(find("AssistReaderList") is Panel readers) || !(find("AssistReadLines") is Panel lines)) return;
            // The same readers with the same words (a new reading arrived, a switch moved): the switches stay the
            // controls they are, so the one the keyboard is on keeps the focus. Anything else builds them again.
            bool same = readers.Children.Count == model.Readers.Count;
            for (int i = 0; same && i < model.Readers.Count; i++)
            {
                same = readers.Children[i] is CheckBox known && Equals(known.Tag, model.Readers[i].ReaderId)
                       && AutomationProperties.GetName(known) == model.Readers[i].Title
                       && AutomationProperties.GetHelpText(known) == (model.Readers[i].Sentence ?? "");
            }
            if (same)
            {
                for (int i = 0; i < model.Readers.Count; i++) ((CheckBox)readers.Children[i]).IsChecked = model.Readers[i].On;
            }
            else
            {
                readers.Children.Clear();
                foreach (AssistReaderRow row in model.Readers) readers.Children.Add(BuildAssistReaderRow(row, onToggle));
            }
            readers.Visibility = model.Readers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (find("AssistNone") is TextBlock none)
            {
                none.Text = model.NothingToRead ?? "";
                none.Visibility = model.NothingToRead != null ? Visibility.Visible : Visibility.Collapsed;
            }
            if (find("AssistReadBlock") is UIElement block) block.Visibility = model.Readers.Count > 0 || model.HasReadings ? Visibility.Visible : Visibility.Collapsed;

            lines.Children.Clear();
            if (model.HasReadings)
            {
                foreach (string line in model.ReadLines)
                    lines.Children.Add(Styled(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }, "BodyText"));
            }
            else
            {
                lines.Children.Add(Styled(new TextBlock { Text = l("Assist_Read_Nothing", "Nothing yet.") }, "HintText"));
            }
            if (find("AssistDeleteButton") is Button delete) delete.IsEnabled = model.HasReadings;
        }

        /// <summary>The style by its key, found where the element ends up in the page (the window's or the app's dictionaries).</summary>
        private static T Styled<T>(T element, string styleKey) where T : FrameworkElement
        {
            element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
            return element;
        }

        /// <summary>One reader: the same switch-with-two-lines the page's other cards use.</summary>
        private static CheckBox BuildAssistReaderRow(AssistReaderRow row, RoutedEventHandler onToggle)
        {
            var title = new TextBlock { Text = row.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            title.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCardTitle");
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var text = new StackPanel();
            text.Children.Add(title);
            if (!string.IsNullOrEmpty(row.Sentence))
                text.Children.Add(Styled(new TextBlock { Text = row.Sentence, Margin = new Thickness(0, 2, 0, 0) }, "HintText"));

            var toggle = new CheckBox
            {
                IsChecked = row.On,
                Content = text,
                Tag = row.ReaderId,
                Margin = new Thickness(0, 0, 0, 10),
            };
            Styled(toggle, "ToggleSwitch");
            AutomationProperties.SetName(toggle, row.Title);
            AutomationProperties.SetHelpText(toggle, row.Sentence ?? "");
            if (onToggle != null)
            {
                toggle.Checked += onToggle;
                toggle.Unchecked += onToggle;
            }
            return toggle;
        }

        private void AssistReaderToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingAssist || !(sender is CheckBox toggle) || !(toggle.Tag is string readerId)) return;
            bool on = toggle.IsChecked == true;
            AssistSettings.SetOn(readerId, on);
            Logger.Log.Info($"In-game helper: {readerId} turned {(on ? "on" : "off")} in Settings.");
        }

        private void AssistDelete_Click(object sender, RoutedEventArgs e)
        {
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            if (game == null) return;
            bool gone = AssistData.DeleteEverything(game);
            _assistDeleteFailedFor = gone ? null : game.Id;
            if (gone) Logger.Log.Info($"In-game helper: everything read for {game.Id} was deleted in Settings.");
            else Logger.Log.Warn($"In-game helper: not everything read for {game.Id} could be deleted (a file is held open).");
            ApplyAssistCard();
        }

        private void AssistSamples_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _syncingAssist) return;
            bool on = AssistSamplesCheckBox.IsChecked == true;
            AssistSettings.SaveSamples = on;
            Logger.Log.Info($"In-game helper: sample pictures turned {(on ? "on" : "off")}.");
        }
    }
}
