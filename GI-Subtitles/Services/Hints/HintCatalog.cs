using System;
using System.Collections.Generic;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.QuickTranslate;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>What a line of the hint card is.</summary>
    public enum HintEntryKind
    {
        /// <summary>A shortcut and what it does, on the sheet.</summary>
        SheetRow,
        /// <summary>The sheet's last line: the way back to it.</summary>
        SheetFooter,
        /// <summary>The answer to a key the player has just pressed.</summary>
        Echo,
        /// <summary>Why no line shows right now.</summary>
        StateTip,
        /// <summary>A shortcut nobody asked about; the only kind with a lifetime budget.</summary>
        TeachingTip,
    }

    /// <summary>A string of the card that names no shortcut: its key in the Strings dictionaries and the English text.</summary>
    public readonly record struct HintText(string Key, string Fallback);

    /// <summary>
    /// One line the card can say. <see cref="KeyId"/> is the shortcut it names (0: none): a sheet row draws the
    /// player's own binding beside its label, every other line holds {0} where the binding goes.
    /// <see cref="LeadKey"/> is a first sentence borrowed from another screen, so the game and Kaption's window never
    /// say two different things.
    /// </summary>
    public sealed record HintEntry(string Id, HintEntryKind Kind, int KeyId, string TextKey, string Fallback,
        string LeadKey = null, string LeadFallback = null, double Seconds = 0);

    /// <summary>
    /// The player's shortcuts as text ("Ctrl+Shift+H"), by hotkey id. A shortcut that is not bound, or that Windows
    /// refused to register, has no text, and nothing that names it is shown.
    /// </summary>
    public readonly struct HintBindings
    {
        private readonly IReadOnlyDictionary<int, string> _byId;

        public HintBindings(IReadOnlyDictionary<int, string> byId) => _byId = byId;

        /// <summary>
        /// Reads the shortcuts the card names, once. <paramref name="describe"/> is
        /// <see cref="HotkeySettingsManager.DescribeBinding"/> in the app; it reads the settings file, so the caller
        /// keeps the result and reads again when the shortcuts change.
        /// </summary>
        public static HintBindings From(Func<int, string> describe)
        {
            var byId = new Dictionary<int, string>(HintCatalog.KeyIds.Count);
            if (describe != null)
                foreach (int id in HintCatalog.KeyIds)
                    if (describe(id) is { Length: > 0 } text) byId[id] = text;
            return new HintBindings(byId);
        }

        /// <summary>"Ctrl+Q" for a bound shortcut, else null.</summary>
        public string Of(int keyId) =>
            _byId != null && _byId.TryGetValue(keyId, out string text) && !string.IsNullOrEmpty(text) ? text : null;

        /// <summary>Whether the line can be said: it names no shortcut, or one the player can press.</summary>
        public bool Allows(HintEntry entry) => entry != null && (entry.KeyId == 0 || Of(entry.KeyId) != null);
    }

    /// <summary>
    /// Everything the in-game hint card says (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.3 and 3.6): the
    /// sheet's rows, the echoes, the tips and the strip's buttons. Local, no network. The card's view reads each text
    /// with its key and falls back to the English here.
    /// </summary>
    public static class HintCatalog
    {
        // The ids HotkeySettingsManager.GetDefaultSettings gives its shortcuts.
        public const int KeyToggleTranslation = 9000, KeyHideSubtitles = 9002, KeyShowDialogueFrame = 9003,
            KeyQuickTranslate = 9005, KeyFixLine = HotkeySettingsManager.FixLineHotkeyId;

        /// <summary>Every shortcut the card names: what <see cref="HintBindings.From"/> reads.</summary>
        public static IReadOnlyList<int> KeyIds { get; } =
            new[] { KeyToggleTranslation, KeyHideSubtitles, KeyShowDialogueFrame, KeyQuickTranslate, KeyFixLine };

        public const double EchoSeconds = 4, ShortEchoSeconds = 2.5;
        /// <summary>A tip stays as long as its text takes to read, within these bounds.</summary>
        public const double MinTipSeconds = 6, MaxTipSeconds = 12;

        public static readonly HintText SheetHeader = new("Hint_Sheet_Header", "Kaption · shortcuts");

        /// <summary>The sheet's rows, in the order they are drawn.</summary>
        public static IReadOnlyList<HintEntry> SheetRows { get; } = new[]
        {
            new HintEntry("key-quick-translate", HintEntryKind.SheetRow, KeyQuickTranslate, "Hint_Key_9005", "Translate any text on screen"),
            new HintEntry("key-hide", HintEntryKind.SheetRow, KeyHideSubtitles, "Hint_Key_9002", "Hide or show subtitles"),
            new HintEntry("key-fix-line", HintEntryKind.SheetRow, KeyFixLine, "Hint_Key_9007", "Fix this line"),
            new HintEntry("key-toggle", HintEntryKind.SheetRow, KeyToggleTranslation, "Hint_Key_9000", "Stop or resume translating"),
        };

        public static readonly HintEntry SheetFooter =
            new("footer", HintEntryKind.SheetFooter, KeyShowDialogueFrame, "Hint_Sheet_Footer", "{0} shows this again");
        /// <summary>The footer when the way back has no shortcut.</summary>
        public static readonly HintText SheetFooterNoKey =
            new("Hint_Sheet_Footer_NoKey", "This list is in Kaption too, on the Shortcuts tab.");

        public static readonly HintEntry EchoSubtitlesHidden = new("echo-subs-hidden", HintEntryKind.Echo, KeyHideSubtitles,
            "Hint_Echo_SubsHidden", "Subtitles hidden. {0} brings them back.", Seconds: EchoSeconds);
        public static readonly HintEntry EchoSubtitlesBack = new("echo-subs-back", HintEntryKind.Echo, 0,
            "Hint_Echo_SubsBack", "Subtitles are back.", Seconds: ShortEchoSeconds);
        public static readonly HintEntry EchoStopped = new("echo-stopped", HintEntryKind.Echo, KeyToggleTranslation,
            "Hint_Echo_Stopped", "Translation stopped. {0} starts it again.", Seconds: EchoSeconds);
        public static readonly HintEntry EchoHintsOff = new("echo-hints-off", HintEntryKind.Echo, 0,
            "Hint_Echo_HintsOff", "Hints are off. Turn them on in Kaption, on the Shortcuts tab.", Seconds: EchoSeconds);

        public static readonly HintEntry TipSubtitlesHidden = new("subs-hidden", HintEntryKind.StateTip, KeyHideSubtitles,
            "Hint_Tip_SubsHidden", "Someone is talking and subtitles are hidden. {0} shows them.");
        // The first sentence of these two is the Start card's own title, read from the same key.
        public static readonly HintEntry TipNoDialogue = new("no-dialogue", HintEntryKind.StateTip, 0,
            "Hint_Tip_NoDialogue", "Talk to someone in the game and the Polish line shows up.",
            LeadKey: "shell_Card_NoDialogue", LeadFallback: "No dialogue seen yet");
        public static readonly HintEntry TipNoMatch = new("no-match", HintEntryKind.StateTip, 0,
            "Hint_Tip_NoMatch", "Check that the game's text is in English.",
            LeadKey: "shell_Card_NoMatch", LeadFallback: "Kaption sees text but can't find the line");
        // Why reads wait, while Kaption keeps the frame on a window that cannot carry it (MainWindow.HoldRead).
        public static readonly HintEntry TipAreaHeldSmall = new("area-held-small", HintEntryKind.StateTip, 0,
            "Hint_Tip_AreaSmall", "The game's window is too small, so Kaption can't read the dialogue. Make it bigger.");
        public static readonly HintEntry TipAreaHeldOff = new("area-held-off", HintEntryKind.StateTip, 0,
            "Hint_Tip_AreaOff", "The game's window is partly off the screen, so Kaption can't read the dialogue. Move it back.");
        public static readonly HintEntry TipQuickTranslate = new("quick-translate", HintEntryKind.TeachingTip, KeyQuickTranslate,
            "Hint_Tip_QuickTranslate", "A quest, an item or a menu in English? Press {0} and mark the text.");
        public static readonly HintEntry TipFixLine = new("fix-line", HintEntryKind.TeachingTip, KeyFixLine,
            "Hint_Tip_FixLine", "A line reads wrong? Press {0} and write a better one. It works right away.");

        /// <summary>The two tips nobody asked for, in the order they are considered.</summary>
        public static IReadOnlyList<HintEntry> TeachingTips { get; } = new[] { TipQuickTranslate, TipFixLine };

        public static readonly HintText StripPin = new("Hint_Strip_Pin", "Keep on screen");
        public static readonly HintText StripUnpin = new("Hint_Strip_Unpin", "Unpin");
        public static readonly HintText StripMove = new("Hint_Strip_Move", "Move to another spot");
        public static readonly HintText StripHide = new("Hint_Strip_Hide", "Hide");
        public static readonly HintText StripNever = new("Hint_Strip_Never", "Don't show again");

        /// <summary>Every line that can name a shortcut.</summary>
        public static IReadOnlyList<HintEntry> Entries { get; } = new[]
        {
            SheetRows[0], SheetRows[1], SheetRows[2], SheetRows[3], SheetFooter,
            EchoSubtitlesHidden, EchoSubtitlesBack, EchoStopped, EchoHintsOff,
            TipSubtitlesHidden, TipNoDialogue, TipNoMatch, TipAreaHeldSmall, TipAreaHeldOff, TipQuickTranslate, TipFixLine,
        };

        /// <summary>Every string without a shortcut in it.</summary>
        public static IReadOnlyList<HintText> Labels { get; } =
            new[] { SheetHeader, SheetFooterNoKey, StripPin, StripUnpin, StripMove, StripHide, StripNever };

        /// <summary>Whether the sheet has anything to show: with no shortcut bound there is no sheet.</summary>
        public static bool HasRows(HintBindings bindings)
        {
            foreach (HintEntry row in SheetRows)
                if (bindings.Allows(row)) return true;
            return false;
        }

        /// <summary>The rows whose shortcut the player can press, in order.</summary>
        public static List<HintEntry> RowsFor(HintBindings bindings)
        {
            var rows = new List<HintEntry>(SheetRows.Count);
            foreach (HintEntry row in SheetRows)
                if (bindings.Allows(row)) rows.Add(row);
            return rows;
        }

        /// <summary>How long a tip of this many characters stays: the Ctrl+Q popup's reading rule, within the tip's bounds.</summary>
        public static double TipSeconds(int chars) =>
            Math.Clamp(ReadingTime.BaseSeconds + Math.Max(0, chars) / ReadingTime.CharsPerSecond, MinTipSeconds, MaxTipSeconds);

        /// <summary>A borrowed first sentence and the tip's own, as one text.</summary>
        public static string Compose(string lead, string text)
        {
            lead = lead?.Trim().TrimEnd('.');
            return string.IsNullOrEmpty(lead) ? text : lead + ". " + text;
        }
    }
}
