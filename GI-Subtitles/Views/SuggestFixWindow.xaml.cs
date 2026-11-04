using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using GI_Subtitles.Services.Community;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "Fix this line": the last subtitle's English, the Polish in use and the speaker, with a box for a better version.
    /// Saving applies it on this PC at once (<see cref="TranslationLayers"/>) and queues it for community review
    /// (<see cref="SuggestionSubmitter"/>); the network never holds the window. Focus goes back to the game on close.
    /// </summary>
    public partial class SuggestFixWindow : Window
    {
        /// <summary>The server's own cap; the per-line limit (3 × English + 40) is a message, not a hard stop.</summary>
        public const int MaxFixLength = SuggestionRules.MaxLength;
        public const int MaxNoteLength = 300;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        internal enum FixDecision
        {
            /// <summary>Empty, or the text already shown: nothing to save.</summary>
            Unchanged,
            /// <summary>Back to what everyone gets: the player's own fix is removed, nothing is sent.</summary>
            Revert,
            Save,
        }

        private readonly ShownLine _line;
        private readonly TranslationLayers _layers;
        private readonly SuggestionSubmitter _submitter;
        private readonly IntPtr _returnFocusTo;
        private readonly string _shown;
        private readonly string _shared;
        private bool _saving;
        private bool _keepFocus;

        /// <param name="returnFocusTo">The game window the hotkey came from; it gets focus back on close.</param>
        public SuggestFixWindow(ShownLine line, TranslationLayers layers, SuggestionSubmitter submitter, IntPtr returnFocusTo)
        {
            InitializeComponent();
            _line = line;
            _layers = layers ?? throw new ArgumentNullException(nameof(layers));
            _submitter = submitter;
            _returnFocusTo = returnFocusTo;
            // What the line reads as now: a fix saved or removed since it was shown counts, not the recorded text.
            string pack = string.IsNullOrEmpty(line.PackPl) ? line.Pl ?? "" : line.PackPl;
            _shown = layers.Resolve(line.EnKey, pack) ?? "";
            _shared = layers.ResolveShared(line.EnKey, pack) ?? "";

            if (!string.IsNullOrWhiteSpace(line.Speaker))
            {
                SpeakerText.Text = line.Speaker.Trim();
                SpeakerText.Visibility = Visibility.Visible;
            }
            EnglishText.Text = line.EnKey ?? "";
            CurrentText.Text = _shown;
            SendHint.SetResourceReference(TextBlock.TextProperty, HintKey(layers.WireGame, layers.Lang, line.EnKey));
            FixBox.MaxLength = MaxFixLength;
            NoteBox.MaxLength = MaxNoteLength;
            FixBox.Text = _shown;
            UpdateSaveEnabled();

            SourceInitialized += (s, e) =>
            {
                // Shown over the game: keep it out of the dialogue capture.
                try { MainWindow.TryExcludeFromCapture(new WindowInteropHelper(this).Handle); }
                catch (Exception ex) { Logger.Log.Warn($"Fix window capture exclusion: {ex.Message}"); }
            };
            Loaded += (s, e) =>
            {
                FixBox.Focus();
                FixBox.CaretIndex = FixBox.Text.Length;
            };
            Closed += (s, e) => ReturnFocus();
        }

        internal ShownLine Line => _line;

        /// <summary>
        /// The line under the boxes: the fix goes to review, or it stays on this PC (the server reviews Polish only, or
        /// takes no suggestions for this line).
        /// </summary>
        internal static string HintKey(string wireGame, string lang, string en) =>
            !SuggestionRules.TakesLanguage(lang) ? "fix_Hint_OtherLanguage"
            : SuggestionRules.CanSuggest(wireGame, lang, en) ? "fix_Hint"
            : "fix_Hint_LocalOnly";

        /// <summary>A plain note: CRLF to LF, trimmed.</summary>
        internal static string Clean(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        /// <summary>Compared the way the server compares proposals: all three cleaned alike, spacing ignored.</summary>
        internal static FixDecision Decide(string en, string typed, string shown, string shared)
        {
            string text = SuggestionRules.Clean(en, typed);
            if (text.Length == 0 || SuggestionRules.SameProposal(text, SuggestionRules.Clean(en, shown))) return FixDecision.Unchanged;
            return SuggestionRules.SameProposal(text, SuggestionRules.Clean(en, shared)) ? FixDecision.Revert : FixDecision.Save;
        }

        /// <summary>Why the server would refuse the fix, in the player's words; null when it is fine.</summary>
        internal static string Problem(string en, string typed)
        {
            switch (SuggestionRules.Check(en, typed, out string detail))
            {
                case SuggestionProblem.TooLong:
                    return L("fix_Error_TooLong", "That's too long for this line. Make it shorter.");
                case SuggestionProblem.LineBreaks:
                    return FormatOr(L("fix_Error_Breaks", "Keep as many line breaks as the English ({0})."), detail);
                case SuggestionProblem.Markup:
                    return FormatOr(L("fix_Error_Markup", "Keep these exactly as in the English: {0}"), detail);
                case SuggestionProblem.ExtraMarkup:
                    return FormatOr(L("fix_Error_Extra", "Remove {0}, the English line doesn't have it."), detail);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Saves (or reverts) the fix, then queues the suggestion; true when one was queued. Disk work only; call off
        /// the UI thread.
        /// </summary>
        internal static bool Apply(FixDecision decision, ShownLine line, TranslationLayers layers, SuggestionSubmitter submitter,
                                   string typed, string note, string shared, DateTimeOffset now)
        {
            if (decision == FixDecision.Unchanged) return false;
            if (decision == FixDecision.Revert)
            {
                layers.RemoveUserFix(line.EnKey);
                submitter?.Withdraw(layers.WireGame, layers.Lang, line.EnKey);
                return false;
            }
            // Stored as the server will store it, so this PC shows what reviewers see.
            string text = SuggestionRules.Clean(line.EnKey, typed);
            string cleanNote = Clean(note);
            string speaker = (line.Speaker ?? "").Trim();
            layers.SaveUserFix(new UserFix(line.EnKey, text, shared, speaker, cleanNote, now));
            // Not for review: a language or game the server does not take, or an English line it refuses.
            if (submitter == null || !SuggestionRules.CanSuggest(layers.WireGame, layers.Lang, line.EnKey)) return false;
            submitter.Submit(new PendingSuggestion
            {
                Game = layers.WireGame,
                Lang = layers.Lang,
                EnText = line.EnKey ?? "",
                CurrentText = shared ?? "",
                SuggestedText = text,
                Speaker = speaker,
                ContextPrev = line.PrevEn ?? "",
                Note = cleanNote,
                CreatedAt = now,
            });
            return true;
        }

        private void FixBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSaveEnabled();

        private void UpdateSaveEnabled()
        {
            var decision = Decide(_line.EnKey, FixBox.Text, _shown, _shared);
            string problem = decision == FixDecision.Save ? Problem(_line.EnKey, FixBox.Text) : null;
            BtnSave.IsEnabled = !_saving && decision != FixDecision.Unchanged && problem == null;
            ShowError(problem);
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>A localised "{0}" template filled in; a translation with a broken placeholder still reads.</summary>
        internal static string FormatOr(string template, string arg)
        {
            try { return string.Format(template ?? "", arg); }
            catch (FormatException) { return (template ?? "").Replace("{0}", arg); }
        }

        /// <summary>True until the player types: a newer line may then replace this one.</summary>
        internal bool IsUntouched =>
            !_saving && FixBox.Text == _shown && string.IsNullOrWhiteSpace(NoteBox.Text);

        /// <summary>Closes for a window with a newer line, which takes the focus instead of the game.</summary>
        internal void CloseForNewerLine()
        {
            _keepFocus = true;
            Close();
        }

        // Ctrl+Enter saves; Enter alone is a new line in the fix box.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && BtnSave.IsEnabled)
            {
                e.Handled = true;
                Save_Click(BtnSave, new RoutedEventArgs());
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_saving) return;
            var decision = Decide(_line.EnKey, FixBox.Text, _shown, _shared);
            if (decision == FixDecision.Unchanged) return;
            if (decision == FixDecision.Save && Problem(_line.EnKey, FixBox.Text) != null) return;
            string typed = FixBox.Text, note = NoteBox.Text;
            SetSaving(true);
            bool queued;
            try
            {
                queued = await Task.Run(() => Apply(decision, _line, _layers, _submitter, typed, note, _shared, DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Fix this line: the fix was not saved: {ex.Message}");
                SetSaving(false);
                ShowError(L("fix_Error_Save", "Couldn't save your version. Try again."));
                return;
            }
            Logger.Log.Info(decision == FixDecision.Revert
                ? "Fix this line: own fix removed, the shared translation applies again."
                : queued
                    ? "Fix this line: fix saved and queued for review."
                    : $"Fix this line: fix saved on this PC only ({_layers.WireGame}/{_layers.Lang} is not reviewed, or the line is not taken).");
            _saving = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void SetSaving(bool saving)
        {
            _saving = saving;
            FixBox.IsReadOnly = saving;
            NoteBox.IsReadOnly = saving;
            BtnCancel.IsEnabled = !saving;
            BtnSave.Content = saving ? L("fix_Saving", "Saving…") : L("fix_Save", "Save");
            UpdateSaveEnabled();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // The save finishes in a moment; closing now would hide its error.
            if (_saving) e.Cancel = true;
            base.OnClosing(e);
        }

        private void ReturnFocus()
        {
            if (_returnFocusTo == IntPtr.Zero || _keepFocus) return;
            try
            {
                if (IsWindow(_returnFocusTo)) SetForegroundWindow(_returnFocusTo);
            }
            catch (Exception ex)
            {
                if (Logger.IsDebugEnabled) Logger.Log.Debug($"Fix window: focus not returned: {ex.Message}");
            }
        }

        private static string L(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
