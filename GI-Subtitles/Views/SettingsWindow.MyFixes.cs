using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.Community;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "Moje poprawki" on Gra i napisy: the player's own fixes for the game and language picked on the page, newest
    /// first, each removable.
    /// </summary>
    public partial class SettingsWindow
    {
        private TranslationLayers _myFixesLayers;
        private string _myFixesCombo;
        private bool _myFixesComboStale = true;
        private int _myFixesRefresh;

        internal sealed class MyFixRow
        {
            public string Key { get; init; }
            public string Text { get; init; }
            public string English { get; init; }
        }

        internal static IReadOnlyList<MyFixRow> MyFixRows(IEnumerable<UserFix> fixes) =>
            (fixes ?? Enumerable.Empty<UserFix>())
                .Select(f => new MyFixRow { Key = f.En, Text = OneLine(f.Text), English = OneLine(f.En) })
                .ToList();

        private static string OneLine(string s) => (s ?? "").Replace("\r\n", " ").Replace('\n', ' ');

        // The card is on a tab: it loads each time the page is shown, so the list is fresh there.
        private void MyFixesCard_Loaded(object sender, RoutedEventArgs e)
        {
            // Read once per showing of the page (the Hotkeys page can change it), not on every refresh.
            _myFixesComboStale = true;
            BindMyFixes(refresh: true);
        }

        /// <summary>
        /// The game or the output language changed while the card is on screen: list that pair's fixes. Off screen
        /// it does nothing; the card binds when it loads.
        /// </summary>
        private void RebindMyFixes()
        {
            if (_myFixesLayers != null) BindMyFixes(refresh: false);
        }

        // The game and language picked on this page, as the rest of it shows them.
        private void BindMyFixes(bool refresh)
        {
            try
            {
                var layers = TranslationLayerRegistry.For(Config.Get("Game", Game) ?? "Genshin", Config.Get("Output", OutputLanguage) ?? "PL");
                if (!ReferenceEquals(layers, _myFixesLayers))
                {
                    DetachMyFixes();
                    _myFixesLayers = layers;
                    layers.Changed += OnMyFixesChanged;
                    SuggestionSubmitter.Shared.QueueChanged += OnMyFixesChanged;
                    // The previous pair's rows must not stay clickable while these load.
                    if (MyFixesList != null) MyFixesList.ItemsSource = null;
                    refresh = true;
                }
                if (refresh) RefreshMyFixes();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"My fixes: list not shown: {ex.Message}");
            }
        }

        private void MyFixesCard_Unloaded(object sender, RoutedEventArgs e) => DetachMyFixes();

        private void DetachMyFixes()
        {
            if (_myFixesLayers == null) return;
            _myFixesLayers.Changed -= OnMyFixesChanged;
            SuggestionSubmitter.Shared.QueueChanged -= OnMyFixesChanged;
            _myFixesLayers = null;
        }

        // Raised from the saving thread; BeginInvoke does not throw once the dispatcher is shutting down.
        private void OnMyFixesChanged(object sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(RefreshMyFixes));

        // The queue and hotkey files are read on a pool thread; only the newest refresh is shown.
        private async void RefreshMyFixes()
        {
            var layers = _myFixesLayers;
            if (layers == null || MyFixesList == null) return;
            int refresh = ++_myFixesRefresh;
            bool readCombo = _myFixesComboStale;
            IReadOnlyList<MyFixRow> rows;
            int pending;
            string combo;
            try
            {
                (rows, pending, combo) = await Task.Run(() =>
                {
                    layers.EnsureLoaded(); // Settings can open before the overlay ever read the fixes
                    return (MyFixRows(layers.UserFixes),
                        SuggestionSubmitter.Shared.Queue.CountFor(layers.WireGame, layers.Lang),
                        readCombo ? HotkeySettingsManager.DescribeBinding(HotkeySettingsManager.FixLineHotkeyId) : null);
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"My fixes: list not refreshed: {ex.Message}");
                return;
            }
            if (refresh != _myFixesRefresh || !ReferenceEquals(layers, _myFixesLayers)) return;
            if (readCombo)
            {
                _myFixesCombo = combo;
                _myFixesComboStale = false;
            }
            MyFixesList.ItemsSource = rows;
            bool any = rows.Count > 0;
            MyFixesList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            MyFixesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            if (!any)
            {
                MyFixesEmpty.Text = _myFixesCombo == null
                    ? L("fix_Mine_Empty_Tray", "No fixes yet. When a subtitle reads wrong, right-click the Kaption icon by the clock and pick Fix the last subtitle.")
                    : FormatOr(L("fix_Mine_Empty", "No fixes yet. When a subtitle reads wrong, press {0} and write your version."), _myFixesCombo);
            }
            MyFixesPending.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (pending > 0)
                MyFixesPending.Text = FormatOr(L("fix_Mine_Pending", "Waiting to be sent: {0}"), pending.ToString());
        }

        private async void MyFixDelete_Click(object sender, RoutedEventArgs e)
        {
            var layers = _myFixesLayers;
            if (!(sender is FrameworkElement button) || !(button.Tag is string key) || layers == null) return;
            button.IsEnabled = false;
            try
            {
                await Task.Run(() =>
                {
                    layers.RemoveUserFix(key);
                    SuggestionSubmitter.Shared.Withdraw(layers.WireGame, layers.Lang, key);
                });
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"My fixes: a fix was not removed: {ex.Message}");
                button.IsEnabled = true;
                ModernDialog.Warn(owner: this, title: L("fix_Mine_Title", "My fixes"),
                    body: L("fix_Error_Delete", "Couldn't remove the fix. Try again."));
                return;
            }
            RefreshMyFixes();
        }

        private static string FormatOr(string template, string arg) => SuggestFixWindow.FormatOr(template, arg);
    }
}
