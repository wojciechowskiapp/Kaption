using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Translation;

namespace GI_Subtitles.Views
{
    /// <summary>The Characters tab is <see cref="CharactersPage"/>; the window only lends it the pack and the game.</summary>
    public partial class SettingsWindow : ICharacterPageHost
    {
        OptimizedMatcher ICharacterPageHost.CharacterPack => Matcher;
        bool ICharacterPageHost.IsCharacterPackReady => IsMatcherReady;
        string ICharacterPageHost.CharacterGame => Config.Get("Game", Game) ?? "Genshin";

        /// <summary>Lower-case, no diacritics ("Łowca" → "lowca"), whitespace kept.</summary>
        internal static string FoldForSearch(string s) => CharactersPage.FoldForSearch(s);

        /// <summary>True when text was built without the now-ready pack (or with an older one).</summary>
        internal static bool NeedsRetranslation(object translatedWith, object current, bool currentReady) =>
            CharactersPage.NeedsRetranslation(translatedWith, current, currentReady);
    }
}
