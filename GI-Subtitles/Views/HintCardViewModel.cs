using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Hints;

namespace GI_Subtitles.Views
{
    /// <summary>One shortcut on the sheet: the player's own combination and what it does.</summary>
    public sealed record HintKeyRow(string Keys, string Label);

    /// <summary>
    /// A sentence that can name a shortcut: the words before the keycap, the combination, the words after.
    /// <see cref="Keys"/> is null for a sentence without one, and the whole text is then in <see cref="Before"/>.
    /// </summary>
    public readonly record struct HintLine(string Before, string Keys, string After)
    {
        private const string Slot = "{0}";

        public bool IsEmpty => string.IsNullOrEmpty(Before) && Keys == null && string.IsNullOrEmpty(After);

        /// <summary>How much there is to read, for the time a tip stays.</summary>
        public int Length => (Before?.Length ?? 0) + (Keys?.Length ?? 0) + (After?.Length ?? 0);

        /// <summary>Splits <paramref name="template"/> at its {0}; without a combination or a slot it stays one text.</summary>
        public static HintLine Of(string template, string keys)
        {
            template ??= "";
            int at = template.IndexOf(Slot, StringComparison.Ordinal);
            if (at < 0) return new HintLine(template, null, "");
            if (string.IsNullOrEmpty(keys)) return new HintLine(template.Remove(at, Slot.Length).Trim(), null, "");
            return new HintLine(template[..at], keys, template[(at + Slot.Length)..]);
        }
    }

    /// <summary>
    /// What the in-game hint card draws (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, 3.3 and 3.6): the sheet
    /// with the player's own shortcuts, or one sentence (the answer to a key, a tip). Built from the catalog and
    /// the bindings as they are now, so a shortcut the player changed shows the new combination, and one that is
    /// not bound is left out. No window, no Config: the texts come through <c>text(key, fallback)</c>.
    /// </summary>
    public sealed class HintCardViewModel
    {
        public bool IsSheet { get; }
        public string Header { get; }
        public IReadOnlyList<HintKeyRow> Rows { get; }
        /// <summary>The sheet's last line: the way back to it.</summary>
        public HintLine Footer { get; }
        /// <summary>The echo or the tip.</summary>
        public HintLine Message { get; }
        /// <summary>The shrinking bar along the bottom: on a card that leaves by itself, except an echo.</summary>
        public bool HasClock { get; }

        private HintCardViewModel(bool isSheet, string header, IReadOnlyList<HintKeyRow> rows, HintLine footer, HintLine message, bool hasClock)
        {
            IsSheet = isSheet;
            Header = header;
            Rows = rows;
            Footer = footer;
            Message = message;
            HasClock = hasClock;
        }

        /// <param name="clock">False for a pinned sheet: it stays.</param>
        public static HintCardViewModel Sheet(HintBindings bindings, Func<string, string, string> text, bool clock)
        {
            ArgumentNullException.ThrowIfNull(text);
            var rows = new List<HintKeyRow>(HintCatalog.SheetRows.Count);
            foreach (HintEntry row in HintCatalog.RowsFor(bindings))
                rows.Add(new HintKeyRow(bindings.Of(row.KeyId), text(row.TextKey, row.Fallback)));

            HintEntry back = HintCatalog.SheetFooter;
            HintLine footer = bindings.Of(back.KeyId) is string keys
                ? HintLine.Of(text(back.TextKey, back.Fallback), keys)
                : new HintLine(text(HintCatalog.SheetFooterNoKey.Key, HintCatalog.SheetFooterNoKey.Fallback), null, "");
            return new HintCardViewModel(true, text(HintCatalog.SheetHeader.Key, HintCatalog.SheetHeader.Fallback), rows, footer, default, clock);
        }

        /// <summary>An echo or a tip. The planner only hands out a line whose shortcut is bound.</summary>
        public static HintCardViewModel Line(HintEntry entry, HintBindings bindings, Func<string, string, string> text)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(text);
            string sentence = text(entry.TextKey, entry.Fallback);
            if (entry.LeadKey != null) sentence = HintCatalog.Compose(text(entry.LeadKey, entry.LeadFallback), sentence);
            HintLine message = HintLine.Of(sentence, entry.KeyId == 0 ? null : bindings.Of(entry.KeyId));
            return new HintCardViewModel(false, null, Array.Empty<HintKeyRow>(), default, message, entry.Kind != HintEntryKind.Echo);
        }
    }
}
