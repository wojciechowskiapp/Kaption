using System;

namespace GI_Subtitles.Services.Community
{
    /// <summary>The last translated line the overlay painted, for "fix this line".</summary>
    public readonly struct ShownLine
    {
        public ShownLine(string game, string lang, string enKey, string pl, string packPl, string speaker, string prevEn, DateTime at)
        {
            Game = game;
            Lang = lang;
            EnKey = enKey;
            Pl = pl;
            PackPl = packPl;
            Speaker = speaker;
            PrevEn = prevEn;
            At = at;
        }

        /// <summary>Desktop game id, also the <c>%APPDATA%\Kaption\&lt;Game&gt;</c> folder.</summary>
        public string Game { get; }
        /// <summary>Lower-case target language.</summary>
        public string Lang { get; }
        /// <summary>The dictionary key that matched.</summary>
        public string EnKey { get; }
        /// <summary>The text shown (fixes applied), before the player's name is filled in.</summary>
        public string Pl { get; }
        /// <summary>The pack's own text for <see cref="EnKey"/>.</summary>
        public string PackPl { get; }
        public string Speaker { get; }
        /// <summary>The key shown before this one, or null.</summary>
        public string PrevEn { get; }
        /// <summary>UTC time the line first appeared.</summary>
        public DateTime At { get; }

        public bool IsEmpty => EnKey == null;
    }

    /// <summary>
    /// Remembers the last shown line. Called on every subtitle tick, so recording is a lock and a few reference
    /// compares into a struct field: nothing is allocated, and a repeat of the same line keeps its time.
    /// </summary>
    public sealed class LastShownLineTracker
    {
        private readonly object _gate = new object();
        private ShownLine _line;

        public void Record(string game, string lang, string enKey, string pl, string packPl, string speaker, DateTime atUtc)
        {
            if (string.IsNullOrEmpty(enKey) || string.IsNullOrEmpty(pl)) return;
            lock (_gate)
            {
                var cur = _line;
                if (cur.EnKey != null && string.Equals(cur.EnKey, enKey, StringComparison.Ordinal))
                {
                    // Same line: take a changed text (a fix was just applied) or a name read a frame late.
                    bool sameText = string.Equals(cur.Pl, pl, StringComparison.Ordinal);
                    bool sameSpeaker = string.IsNullOrEmpty(speaker) || string.Equals(cur.Speaker, speaker, StringComparison.Ordinal);
                    if (sameText && sameSpeaker) return;
                    _line = new ShownLine(cur.Game, cur.Lang, cur.EnKey, pl, packPl ?? cur.PackPl,
                        string.IsNullOrEmpty(speaker) ? cur.Speaker : speaker, cur.PrevEn, cur.At);
                    return;
                }
                _line = new ShownLine(game, lang, enKey, pl, packPl, speaker ?? "", cur.EnKey, atUtc);
            }
        }

        public bool TryGet(out ShownLine line)
        {
            lock (_gate) line = _line;
            return !line.IsEmpty;
        }
    }
}
