using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Guess
{
    /// <summary>A ranking notice ready to show: the balloon's title and body (the Start strip shows the body).</summary>
    public sealed record GuessNotice(string Title, string Body, string Board);

    /// <summary>
    /// The words of a ranking notice and the site links (R4 section 2.5). Pure: the strings come in through
    /// <c>L(key, fallback)</c>, so tests run both dictionaries without a window.
    /// </summary>
    public static class GuessNoticeText
    {
        /// <summary>The game's guess name ("Gendle"), then its short name; null for an unknown board.</summary>
        public static string GuessNameOf(string board)
        {
            var m = GameRegistry.Current.FindByWireId(board);
            if (m == null) return null;
            return !string.IsNullOrWhiteSpace(m.Site?.GuessName) ? m.Site.GuessName
                : !string.IsNullOrWhiteSpace(m.ShortName) ? m.ShortName : m.Name;
        }

        /// <summary>Every listed game's guess name in UI order, for the Help card ("Gendle, Stardle, Zendle and Wuwdle").</summary>
        public static IReadOnlyList<string> AllGuessNames() =>
            GameRegistry.Current.Listed.Select(m => GuessNameOf(m.Id)).Where(n => !string.IsNullOrEmpty(n)).ToList();

        /// <summary>"A, B, C and D" with the language's "and".</summary>
        public static string JoinNames(IReadOnlyList<string> names, string and)
        {
            if (names == null || names.Count == 0) return string.Empty;
            if (names.Count == 1) return names[0];
            return string.Join(", ", names.Take(names.Count - 1)) + and + names[names.Count - 1];
        }

        /// <summary>How the public board names a player: the nickname, else "Player 4eb5d1"; null when hidden or unnamed.</summary>
        public static string PublicName(GuessNeighbour n, Func<string, string, string> L)
        {
            if (n == null || n.Hidden) return null;
            if (!string.IsNullOrWhiteSpace(n.Name)) return n.Name.Trim();
            if (!string.IsNullOrWhiteSpace(n.Tag))
                return Format(L("GuessNotify_Pseudonym_Format", "Player {0}"), "Player {0}", n.Tag.Trim());
            return null;
        }

        public static GuessNotice Build(GuessOvertake o, Func<string, string, string> L)
        {
            if (o == null) return null;
            string boardName = o.IsGlobal ? L("GuessNotify_Board_Global", "overall") : GuessNameOf(o.Board) ?? o.Board;
            string title = o.IsGlobal
                ? L("GuessNotify_Title_Global", "Someone passed you in the overall ranking")
                : Format(L("GuessNotify_Title", "Someone passed you in {0}"), "Someone passed you in {0}", boardName);
            string above = PublicName(o.Above, L);
            string body = above != null
                ? Format(L("GuessNotify_Body_Format", "You're now #{1} on this month's {0} board, down from #{2}. Just ahead of you: {3}."),
                    "You're now #{1} on this month's {0} board, down from #{2}. Just ahead of you: {3}.", boardName, o.To, o.From, above)
                : Format(L("GuessNotify_Body_NoName_Format", "You're now #{1} on this month's {0} board, down from #{2}."),
                    "You're now #{1} on this month's {0} board, down from #{2}.", boardName, o.To, o.From);
            return new GuessNotice(Clip(title, 63), Clip(body, 255), o.Board);
        }

        // A translation whose placeholders do not parse falls back to the English.
        private static string Format(string format, string fallback, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return string.Format(CultureInfo.InvariantCulture, fallback, args); }
        }

        // Shell_NotifyIcon caps the title at 63 and the text at 255 characters.
        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    /// <summary>The kaption.one pages the app opens for the guess game, by interface language.</summary>
    public static class GuessLinks
    {
        public const string Host = "kaption.one";

        /// <summary>The ranked lobby: where a ranking notice leads.</summary>
        public static string RankedLobby(string uiLanguage) =>
            IsPolish(uiLanguage) ? "https://kaption.one/pl/zgadnij/rankingowa/" : "https://kaption.one/guess/ranked/";

        /// <summary>The guess game's index: where "Play" leads.</summary>
        public static string Play(string uiLanguage) =>
            IsPolish(uiLanguage) ? "https://kaption.one/pl/zgadnij/" : "https://kaption.one/guess/";

        /// <summary>Only https pages on kaption.one are opened from a notice.</summary>
        public static bool IsSitePage(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri u) && u.Scheme == Uri.UriSchemeHttps
            && string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase);

        private static bool IsPolish(string lang) => lang != null && lang.StartsWith("pl", StringComparison.OrdinalIgnoreCase);
    }
}
