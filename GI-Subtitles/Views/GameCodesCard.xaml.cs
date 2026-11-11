using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The Start page's codes card. It only draws a <see cref="CodesCardView"/>; reading the list is CodesPoller's job
    /// (SettingsWindow.Codes.cs). The host handles the links (<see cref="OpenUrl"/>) and remembers the fold
    /// (<see cref="FoldChanged"/>). Copying puts the code on the clipboard and nothing else: no notice, no sound.
    /// </summary>
    public partial class GameCodesCard : UserControl
    {
        /// <summary>How long a Copy button says "Copied".</summary>
        public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);
        /// <summary>Clipboard tries when another app holds it (CLIPBRD_E_CANT_OPEN), and the pause between them.</summary>
        public const int ClipboardAttempts = 4;
        public static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(60);
        private const string CopiedTag = "copied";

        private string _allCodesUrl;

        /// <summary>A link was clicked: the codes page or a redeem page.</summary>
        public event Action<string> OpenUrl;
        /// <summary>The player folded (true) or opened (false) the list.</summary>
        public event Action<bool> FoldChanged;

        /// <summary>The names of a row's two buttons in the row template (GameCodesCard.xaml).</summary>
        public const string CopyPart = "copy", RedeemPart = "redeem";

        // Which button of which code has the keyboard, as the rows' own focus events say it.
        private Button _focusButton;
        private string _focusCode, _focusPart;
        /// <summary>Gives the keyboard to a control. A seam: a test watches what the card asks for.</summary>
        public Func<UIElement, bool> FocusControl { get; set; } = control => control.Focus();

        public GameCodesCard()
        {
            InitializeComponent();
            Rows.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Rows_GotKeyboardFocus), true);
            Rows.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(Rows_LostKeyboardFocus), true);
        }

        private void Rows_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is Button button && button.DataContext is CodeRowView row)
            {
                _focusButton = button;
                _focusCode = row.Code;
                _focusPart = button.Name;
            }
        }

        // The player moved on to a control outside the rows: nothing to give back. A button that only vanished (its row
        // moved) reports no such move while the rows are being changed, so it stays remembered.
        private void Rows_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus != null && !(e.NewFocus is System.Windows.Media.Visual to && Rows.IsAncestorOf(to))) ForgetFocus();
        }

        private void ForgetFocus()
        {
            _focusButton = null;
            _focusCode = _focusPart = null;
        }

        public CodesCardView Current { get; private set; }

        /// <summary>The words of a code's dates from the interface's resources, for <see cref="CodesCardModel.Build"/>.</summary>
        public static CodeDateWords DateWords(Func<string, string, string> L)
        {
            CodeDateWords en = CodeDateWords.English;
            return L == null ? en : new CodeDateWords(L("codes_Added", en.Added), L("codes_Listed", en.Listed), L("codes_Until", en.Until),
                L("codes_NoEnd", en.NoEnd), L("codes_EndsIn", en.EndsIn), L("codes_Days", en.Days));
        }

        public bool IsFolded => FoldToggle.IsChecked != true;

        /// <summary>Shows <paramref name="view"/>, or hides the card when there is nothing to redeem.</summary>
        public void Apply(CodesCardView view, bool folded, Func<string, string, string> L, CultureInfo culture)
        {
            Current = view;
            if (view == null || !view.Visible)
            {
                Visibility = Visibility.Collapsed;
                Rows.ItemsSource = null;
                return;
            }
            culture ??= CultureInfo.CurrentUICulture;
            TitleRun.Text = string.Format(culture, L("codes_Title", "{0} codes"), view.GameName);
            CountRun.Text = "  ·  " + Plural.Format(L("codes_Count", "{0} active|{0} active|{0} active"), view.ActiveCount, culture);
            ShowRows(view.Rows);
            _allCodesUrl = view.AllCodesUrl;
            AllCodesButton.Visibility = string.IsNullOrEmpty(view.AllCodesUrl) ? Visibility.Collapsed : Visibility.Visible;
            AllCodesButton.Content = view.MoreCount > 0
                ? string.Format(culture, L("codes_All_More", "All codes (+{0})"), view.MoreCount)
                : L("codes_All", "All codes");
            AllCodesButton.ToolTip = view.AllCodesUrl;

            if (view.InGamePath is { Count: > 0 } path)
            {
                string steps = string.Join(" › ", path);
                InGameHint.Text = view.InGameUnlock == null
                    ? string.Format(culture, L("codes_InGame", "Enter it in the game: {0}."), steps)
                    : string.Format(culture, L("codes_InGame_Unlock", "Enter it in the game: {0} (unlocks at {1})."), steps, view.InGameUnlock);
                InGameHint.Visibility = Visibility.Visible;
            }
            else
            {
                InGameHint.Visibility = Visibility.Collapsed;
            }
            SetFolded(folded, L);
            Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Puts <paramref name="rows"/> on screen. The same codes in the same order are updated in place (a countdown that
        /// dropped an hour, a "New" mark that ran out), so a focused button keeps focus and a "Copied" button stays so; only a
        /// changed list of codes replaces the rows.
        /// </summary>
        private void ShowRows(IReadOnlyList<CodesCardRow> rows)
        {
            if (Rows.ItemsSource is not ObservableCollection<CodeRowView> shown)
            {
                ForgetFocus();
                Rows.ItemsSource = new ObservableCollection<CodeRowView>(rows.Select(r => new CodeRowView(r)));
                return;
            }
            // Row by row, matched by code: a code already shown keeps its row object (moved when its place changed), a new
            // one is put in, one no longer listed goes. Only a row that moves or goes loses its controls.
            string focusCode = _focusCode, focusPart = _focusPart;
            bool reshaped = false;
            for (int i = 0; i < rows.Count; i++)
            {
                int at = -1;
                for (int j = i; j < shown.Count && at < 0; j++) if (shown[j].Code == rows[i].Code) at = j;
                if (at < 0) { shown.Insert(i, new CodeRowView(rows[i])); reshaped = true; continue; }
                if (at != i) { shown.Move(at, i); reshaped = true; }
                shown[i].Update(rows[i]);
            }
            while (shown.Count > rows.Count) { shown.RemoveAt(shown.Count - 1); reshaped = true; }
            if (reshaped && focusCode != null) RestoreFocus(shown, focusCode, focusPart);
        }

        /// <summary>
        /// After the rows changed shape: the keyboard goes back to the same button of the same code, wherever its row is
        /// now. When that code is gone it goes to the first row's Copy, and with no rows left to "All codes".
        /// </summary>
        private void RestoreFocus(ObservableCollection<CodeRowView> shown, string code, string part)
        {
            if (_focusButton is { IsKeyboardFocused: true } && Rows.IsAncestorOf(_focusButton)) return;
            Rows.UpdateLayout();   // a moved row's controls are made again here
            CodeRowView same = shown.FirstOrDefault(r => r.Code == code);
            CodeRowView target = same ?? shown.FirstOrDefault();
            Button button = target == null ? null : (same != null ? RowButton(target, part) : null) ?? RowButton(target, CopyPart);
            UIElement to = (UIElement)button ?? AllCodesButton;
            if (to == null || !FocusControl(to)) return;
            if (button != null) { _focusButton = button; _focusCode = target.Code; _focusPart = button.Name; }
            else ForgetFocus();
        }

        /// <summary>A row's Copy or Redeem button as the row template made it; null while it is not on screen.</summary>
        public Button RowButton(CodeRowView row, string part)
        {
            if (Rows.ItemContainerGenerator.ContainerFromItem(row) is not ContentPresenter host) return null;
            host.ApplyTemplate();
            return host.ContentTemplate?.FindName(part, host) is Button { Visibility: Visibility.Visible } button ? button : null;
        }

        private void SetFolded(bool folded, Func<string, string, string> L)
        {
            FoldToggle.IsChecked = !folded;
            Body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            string tip = folded ? L("codes_Unfold", "Show codes") : L("codes_Fold", "Hide codes");
            FoldToggle.ToolTip = tip;
            AutomationProperties.SetName(FoldToggle, tip);
        }

        private string Text(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void FoldToggle_Click(object sender, RoutedEventArgs e)
        {
            bool folded = FoldToggle.IsChecked != true;
            SetFolded(folded, Text);
            FoldChanged?.Invoke(folded);
        }

        private void AllCodes_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_allCodesUrl)) OpenUrl?.Invoke(_allCodesUrl);
        }

        private void Redeem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.CommandParameter is string url && url.StartsWith("https://", StringComparison.Ordinal))
                OpenUrl?.Invoke(url);
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not string code || code.Length == 0) return;
            if (!await TryCopyAsync(code)) return;
            button.Tag = CopiedTag;
            AutomationProperties.SetName(button, Text("codes_Copied", "Copied"));
            var timer = new DispatcherTimer { Interval = CopiedFor };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                button.ClearValue(TagProperty);
                AutomationProperties.SetName(button, Text("codes_Copy_Tip", "Copy the code"));
            };
            timer.Start();
        }

        /// <summary>One row as the template binds it: a <see cref="CodesCardRow"/> whose changing parts notify.</summary>
        public sealed class CodeRowView : INotifyPropertyChanged
        {
            private CodesCardRow _row;
            public CodeRowView(CodesCardRow row) => _row = row;
            public event PropertyChangedEventHandler PropertyChanged;
            public string Code => _row.Code;
            public string Rewards => _row.Rewards;
            public string RedeemUrl => _row.RedeemUrl;
            public string Dates => _row.Dates;
            public bool IsNew => _row.IsNew;
            public string EndsIn => _row.EndsIn;

            /// <summary>Takes the same code's newer row; only what differs is announced.</summary>
            public void Update(CodesCardRow row)
            {
                CodesCardRow old = _row;
                _row = row;
                if (old.Rewards != row.Rewards) Changed(nameof(Rewards));
                if (old.RedeemUrl != row.RedeemUrl) Changed(nameof(RedeemUrl));
                if (old.Dates != row.Dates) Changed(nameof(Dates));
                if (old.IsNew != row.IsNew) Changed(nameof(IsNew));
                if (old.EndsIn != row.EndsIn) Changed(nameof(EndsIn));
            }

            private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>Puts the code on the clipboard, retrying while another app holds it; false (logged at Info) when it never frees.</summary>
        private static async Task<bool> TryCopyAsync(string code)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Clipboard.SetText(code);
                    return true;
                }
                catch (ExternalException ex)
                {
                    if (attempt >= ClipboardAttempts)
                    {
                        Logger.Log.Info($"Codes: the clipboard stayed busy after {attempt} tries ({ex.Message}).");
                        return false;
                    }
                }
                await Task.Delay(ClipboardRetryDelay);
            }
        }
    }
}
