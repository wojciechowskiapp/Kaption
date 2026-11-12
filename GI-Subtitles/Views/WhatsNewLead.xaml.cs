using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Services.Notices.WhatsNew;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The Start page's "New in Kaption" card. It only draws rows and reports what the player clicked; which rows, what
    /// is remembered and what a switch does belong to the host (SettingsWindow.WhatsNew.cs, Services/Notices/WhatsNew).
    /// </summary>
    public partial class WhatsNewLead : UserControl
    {
        /// <summary>One row on the card, with the state the template shows.</summary>
        public sealed class RowModel : INotifyPropertyChanged
        {
            private bool _done, _switchOn;

            public RowModel(WhatsNewRow row, Geometry icon, bool list, bool first, Func<string, string, string> l)
            {
                Row = row;
                Icon = icon;
                List = list;
                First = first;
                _switchOn = row.FeatureOn;
                TurnOnName = l("WhatsNew_TurnOn", "Turn on") + ": " + row.Title;
                NotNowName = l("WhatsNew_NotNow", "Not now") + ": " + row.Title;
                NeverName = l("WhatsNew_Never", "Don't ask again") + ": " + row.Title;
            }

            public WhatsNewRow Row { get; }
            public Geometry Icon { get; }
            public bool List { get; }
            public bool First { get; }
            public string Title => Row.Title;
            public string Body => Row.Body;
            public string Done => Row.Done;
            public string ActionLabel => Row.ActionLabel;
            public string TurnOnName { get; }
            public string NotNowName { get; }
            public string NeverName { get; }

            /// <summary>The player turned it on from this card.</summary>
            public bool IsDone
            {
                get => _done;
                set
                {
                    if (_done == value) return;
                    _done = value;
                    Changed(nameof(IsDone), nameof(AskVisibility), nameof(DoneVisibility));
                }
            }

            public bool SwitchOn
            {
                get => _switchOn;
                set
                {
                    if (_switchOn == value) return;
                    _switchOn = value;
                    Changed(nameof(SwitchOn));
                }
            }

            public Visibility RuleVisibility => First ? Visibility.Collapsed : Visibility.Visible;
            public Visibility AskVisibility => !List && Row.IsAsk && !IsDone ? Visibility.Visible : Visibility.Collapsed;
            public Visibility DoneVisibility => !List && IsDone && !string.IsNullOrEmpty(Done) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ActionVisibility => !string.IsNullOrEmpty(ActionLabel) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility SwitchVisibility => List && Row.HasSwitch ? Visibility.Visible : Visibility.Collapsed;

            public event PropertyChangedEventHandler PropertyChanged;

            private void Changed(params string[] names)
            {
                foreach (string n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            }
        }

        private readonly ObservableCollection<RowModel> _rows = new();
        private Func<string, string, string> _l = (_, fallback) => fallback;

        /// <summary>"Turn on" on a question.</summary>
        public event Action<WhatsNewRow> TurnOnRequested;
        /// <summary>"Not now" on a question.</summary>
        public event Action<WhatsNewRow> NotNowRequested;
        /// <summary>"Don't ask again" on a question.</summary>
        public event Action<WhatsNewRow> NeverRequested;
        /// <summary>A row's own action (open today's card).</summary>
        public event Action<WhatsNewRow> ActionRequested;
        /// <summary>A switch flipped in the list; the host answers with <see cref="SetSwitch"/>.</summary>
        public event Action<WhatsNewRow, bool> SwitchRequested;
        /// <summary>"Got it", or "Close" in the list.</summary>
        public event Action CloseRequested;
        /// <summary>The last row left the card.</summary>
        public event Action Emptied;

        public WhatsNewLead()
        {
            InitializeComponent();
            Rows.ItemsSource = _rows;
        }

        /// <summary>The card shows the whole list (opened by the player), not the rows of a release.</summary>
        public bool IsList { get; private set; }

        public IReadOnlyList<RowModel> Shown => _rows;

        /// <summary>Shows <paramref name="rows"/>, or hides the card when there are none.</summary>
        public void Apply(IReadOnlyList<WhatsNewRow> rows, bool list, Func<string, string, string> l)
        {
            IsList = list;
            _rows.Clear();
            if (rows == null || rows.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                return;
            }
            _l = l ?? ((_, fallback) => fallback);
            l = _l;
            for (int i = 0; i < rows.Count; i++) _rows.Add(new RowModel(rows[i], IconOf(rows[i].Entry.Icon), list, i == 0, l));
            Heading.Text = list ? l("WhatsNew_List_Eyebrow", "What's new in Kaption") : l("WhatsNew_Eyebrow", "New in Kaption");
            string close = list ? l("WhatsNew_Close", "Close") : l("WhatsNew_GotIt", "Got it");
            DoneButton.Content = close;
            // In the list the player is already where the footer points to.
            FooterHint.Visibility = list ? Visibility.Hidden : Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        /// <summary>The row's thing is on now: its buttons give way to the line that says what happens next.</summary>
        public void MarkDone(WhatsNewRow row)
        {
            RowModel model = _rows.FirstOrDefault(r => ReferenceEquals(r.Row, row));
            if (model == null) return;
            // No words for it: the row has said all it had to say.
            if (string.IsNullOrEmpty(model.Done)) Remove(row);
            else model.IsDone = true;
        }

        /// <summary>Takes the row off the card (answered "not now" or "never").</summary>
        public void Remove(WhatsNewRow row)
        {
            RowModel model = _rows.FirstOrDefault(r => ReferenceEquals(r.Row, row));
            if (model == null) return;
            int at = _rows.IndexOf(model);
            _rows.RemoveAt(at);
            // The first row carries no rule above it; whichever row is first now must not either.
            if (at == 0 && _rows.Count > 0 && !_rows[0].First)
                _rows[0] = new RowModel(_rows[0].Row, _rows[0].Icon, _rows[0].List, true, _l)
                {
                    IsDone = _rows[0].IsDone,
                    SwitchOn = _rows[0].SwitchOn,
                };
            if (_rows.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                Emptied?.Invoke();
            }
        }

        /// <summary>Shows a switch as it really is after the host tried to flip it.</summary>
        public void SetSwitch(WhatsNewRow row, bool on)
        {
            RowModel model = _rows.FirstOrDefault(r => ReferenceEquals(r.Row, row));
            if (model != null) model.SwitchOn = on;
        }

        /// <summary>
        /// Every switch shows what is true now: the same thing may have been flipped elsewhere (its own switch in
        /// Settings) while the list was open.
        /// </summary>
        public void SyncSwitches(Func<string, bool> featureOn)
        {
            if (featureOn == null) return;
            foreach (RowModel model in _rows)
                if (model.Row.HasSwitch) model.SwitchOn = featureOn(model.Row.Entry.Feature);
        }

        private Geometry IconOf(string name) => TryFindResource(name switch
        {
            "hourglass" => "WnHourglass",
            "calendar" => "WnCalendar",
            "power" => "WnPower",
            "record" => "WnRecord",
            _ => "WnSpark",
        }) as Geometry;

        private static RowModel ModelOf(object sender) => (sender as FrameworkElement)?.DataContext as RowModel;

        private void TurnOn_Click(object sender, RoutedEventArgs e)
        {
            if (ModelOf(sender) is RowModel m) TurnOnRequested?.Invoke(m.Row);
        }

        private void NotNow_Click(object sender, RoutedEventArgs e)
        {
            if (ModelOf(sender) is RowModel m) NotNowRequested?.Invoke(m.Row);
        }

        private void Never_Click(object sender, RoutedEventArgs e)
        {
            if (ModelOf(sender) is RowModel m) NeverRequested?.Invoke(m.Row);
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (ModelOf(sender) is RowModel m) ActionRequested?.Invoke(m.Row);
        }

        // The binding is one way: the switch shows what the host says is true. A flip away from that is the
        // player's, however it was made: a click, the space bar, or a screen reader's Toggle, which raises no Click.
        // A change that agrees with the row is the binding drawing the switch or following its setting: no request.
        private void Switch_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || ModelOf(sender) is not RowModel m) return;
            bool wanted = box.IsChecked == true;
            if (wanted == m.SwitchOn) return;
            // Shown as it really is until the host says the setting took. SetCurrentValue keeps the binding.
            box.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, m.SwitchOn);
            SwitchRequested?.Invoke(m.Row, wanted);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    }
}
