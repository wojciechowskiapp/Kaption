using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Cards;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// "Karta buildu": Start's Skróty tile and the Characters header open the build card maker on kaption.one with the
    /// player's UID, read off the game screen (Services/Cards/UidReader) or remembered from the last read. No message
    /// box: one quiet line under the entry while it reads, and when no UID could be read (the page then asks for it) or
    /// the text reader is still loading. Start › Zaawansowane shows what is remembered and forgets it.
    /// </summary>
    public partial class SettingsWindow
    {
        private bool _buildCardBusy;

        /// <summary>Opens a URL in the browser; tests put a recorder here.</summary>
        internal Action<string> OpenUrl { get; set; } = OpenUrlInBrowser;

        /// <summary>Reads the selected game's UID off the screen; tests replace the screen and the reader.</summary>
        internal Func<string, Task<UidReadResult>> ReadUidFromScreen { get; set; }

        /// <summary>
        /// The screen read under the app's own reader: the game, the text reader (null while it is not ready), the screen
        /// rectangles to black out and the time budget go in, the result comes back. Tests put a recorder here.
        /// </summary>
        internal Func<string, PaddleOCRSharp.PaddleOCREngine, IReadOnlyList<System.Drawing.Rectangle>, TimeSpan, UidReadResult> ReadGameScreen { get; set; }
            = (game, reader, exclude, budget) => UidReader.ReadFromGame(game, reader, exclude, budget);

        /// <summary>
        /// Where Kaption draws over the game besides this window, in screen pixels: the subtitle panel while it shows and
        /// the Ctrl+Q popup while it is open. MainWindow sets this; the UID read blacks them out with this window.
        /// </summary>
        internal Func<IReadOnlyList<System.Drawing.Rectangle>> OverlayScreenAreas { get; set; }

        private async void BuildCard_Click(object sender, RoutedEventArgs e) => await OpenBuildCardAsync(ShowStartBuildCardNote);

        Task ICharacterPageHost.OpenBuildCardAsync(Action<string> showNote) => OpenBuildCardAsync(showNote);

        /// <summary>
        /// Opens the maker for the selected game. <paramref name="showNote"/> gets the line to show under the entry, or
        /// null to hide it. A second click while the first one still reads is ignored.
        /// </summary>
        internal async Task OpenBuildCardAsync(Action<string> showNote)
        {
            if (_buildCardBusy) return;
            _buildCardBusy = true;
            try
            {
                string game = GameRegionProfile.Get(Config.Get("Game", "Genshin") ?? "Genshin").GameId;
                if (BuildCardMemory.Recall(game) == null)
                    showNote?.Invoke(L("card_Reading", "Reading the UID from the game screen…"));
                var reader = ReadUidFromScreen ?? ReadUidOffTheScreenAsync;
                BuildCardTarget target = await BuildCardEntry.ResolveAsync(game, SiteLanguage(), () => reader(game));
                OpenUrl?.Invoke(target.Url);
                showNote?.Invoke(BuildCardLine(target, game, Engine == EngineStatus.Failed, L));
                string how = target.Source switch
                {
                    UidSource.Remembered => "the remembered UID",
                    UidSource.Screen => "the UID read from the screen",
                    _ => "no UID",
                };
                Logger.Log.Info($"Build card opened for {game} with {how}.");
                RefreshBuildCardMemory();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Build card: {ex.GetType().Name}: {ex.Message}");
                showNote?.Invoke(null);
            }
            finally
            {
                _buildCardBusy = false;
            }
        }

        /// <summary>
        /// The line under the entry once the page is open: none when it opened with a UID. When the text reader was not
        /// ready because it is still loading, that is what the line says (after a failed start the plain line is the true
        /// one). Otherwise the plain line, which for Star Rail and ZZZ adds that those games show the UID in their menu.
        /// Static so the tests run the app's own choice.
        /// </summary>
        internal static string BuildCardLine(BuildCardTarget target, string gameId, bool readerFailed, Func<string, string, string> L)
        {
            if (target.Uid != null) return null;
            if (target.ReadOutcome == UidReadOutcome.NoReader && !readerFailed)
                return L("card_Reader_Loading", "The text reader is still loading. Try again in a moment.");
            string line = L("card_Uid_NotRead", "Couldn't read the UID from the game screen. Type it on the page.");
            if (!UidReader.ShowsUidOnlyInMenus(gameId)) return line;
            string menu = L("card_Uid_MenuHint", "In Star Rail and ZZZ open the game menu (Esc), the UID is shown there.");
            // Chinese and Japanese sentences end on a full-width stop and run on without a space.
            return line.EndsWith(FullWidthStop) ? line + menu : line + " " + menu;
        }

        /// <summary>U+3002, the full stop Chinese and Japanese end a sentence with.</summary>
        private const char FullWidthStop = (char)0x3002;

        // The page's language follows the strings in use, like the Characters page's own link.
        private string SiteLanguage() => TryFindResource("Char_SiteLocale") as string ?? UiLanguage.Current;

        /// <summary>
        /// The app's reader: the shared engine once it is ready. Kaption's own windows over the game are blacked out of the
        /// capture (this one, the subtitle panel, the Ctrl+Q popup), and while translating the read gets the longer budget,
        /// because the dialogue pass under way when the click came may still hold the reader.
        /// </summary>
        private Task<UidReadResult> ReadUidOffTheScreenAsync(string game)
        {
            var reader = Engine == EngineStatus.Ready ? engine : null;
            var exclude = CaptureExclusions();
            bool translating = IsOcrRunning?.Invoke() == true;
            TimeSpan budget = UidReader.BudgetFor(translating);
            var read = ReadGameScreen ?? ((g, r, x, b) => UidReader.ReadFromGame(g, r, x, b));
            return Task.Run(() =>
            {
                UidReadResult result = read(game, reader, exclude, budget);
                Logger.Log.Info($"Build card UID read for {game}: {result.Outcome}" +
                                (result.Corner == UidCorner.None ? "" : $" ({result.Corner})") +
                                $" in {result.Elapsed.TotalMilliseconds:0} ms of a {budget.TotalMilliseconds:0} ms budget" +
                                (translating ? " (translating)" : "") + $", {exclude.Count} area(s) masked.");
                return result;
            });
        }

        /// <summary>This window's rectangle and the overlay's (the subtitle panel, the Ctrl+Q popup), in one list.</summary>
        private IReadOnlyList<System.Drawing.Rectangle> CaptureExclusions()
        {
            var areas = new List<System.Drawing.Rectangle>(OwnWindowArea());
            try
            {
                foreach (var area in OverlayScreenAreas?.Invoke() ?? Array.Empty<System.Drawing.Rectangle>())
                    if (area.Width > 0 && area.Height > 0) areas.Add(area);
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Build card: overlay bounds unavailable: {ex.Message}");
            }
            return areas;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowBounds
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WindowBounds rect);

        /// <summary>This window in screen pixels when it is on screen (it may sit over the game's corner).</summary>
        private IReadOnlyList<System.Drawing.Rectangle> OwnWindowArea()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || !IsVisible || WindowState == WindowState.Minimized || !GetWindowRect(hwnd, out var r))
                    return Array.Empty<System.Drawing.Rectangle>();
                return new[] { System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) };
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"Build card: own window bounds unavailable: {ex.Message}");
                return Array.Empty<System.Drawing.Rectangle>();
            }
        }

        /// <summary>The line under the Start tile: reading, the UID could not be read, or nothing (null).</summary>
        private void ShowStartBuildCardNote(string text)
        {
            if (BuildCardNote == null) return;
            BuildCardNote.Text = text ?? string.Empty;
            BuildCardNote.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ── Start › Zaawansowane: what is remembered, and "Zapomnij UID" ──

        private void RefreshBuildCardMemory() =>
            ApplyBuildCardMemoryTo(FindName, BuildCardMemory.All(), L, CultureInfo.CurrentUICulture);

        /// <summary>
        /// Fills the build card row under Start › Zaawansowane: the remembered UIDs by game, and "Zapomnij UID" enabled
        /// only when there is one. Static so the render tests run the app's code.
        /// </summary>
        internal static void ApplyBuildCardMemoryTo(Func<string, object> find, IReadOnlyList<(string GameId, string Uid)> remembered,
            Func<string, string, string> L, CultureInfo culture)
        {
            remembered ??= Array.Empty<(string, string)>();
            if (find("BuildCardMemoryText") is TextBlock text)
                text.Text = remembered.Count == 0
                    ? L("card_Memory_None", "No UID saved. Next time Kaption reads it from the game screen.")
                    : string.Format(culture, L("card_Memory_Format", "Saved: {0}"),
                        string.Join(" · ", remembered.Select(r => $"{GameRegionProfile.DisplayNameOf(r.GameId)} {r.Uid}")));
            if (find("BuildCardForgetButton") is UIElement forget) forget.IsEnabled = remembered.Count > 0;
        }

        private void BuildCardForget_Click(object sender, RoutedEventArgs e)
        {
            int forgotten = BuildCardMemory.ForgetAll();
            Logger.Log.Info($"Build card: forgot {forgotten} remembered UID(s).");
            RefreshBuildCardMemory();
            if (forgotten > 0 && BuildCardMemoryText != null)
                BuildCardMemoryText.Text = L("card_Forget_Done", "Forgotten. Next time Kaption reads the UID from the game screen.");
            ShowStartBuildCardNote(null);
        }
    }
}
