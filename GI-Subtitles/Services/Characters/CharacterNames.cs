using System;

namespace GI_Subtitles.Services.Characters;

/// <summary>
/// Character names are shown as the English proper name. Packs carry machine translations of
/// them ("Brzytwa" for Razor), so only the protagonist title gets the subtitles' Polish word,
/// and only as a secondary gloss.
/// </summary>
public static class CharacterNames
{
    private static readonly string[] GlossedTitles = ["Traveler", "Trailblazer"];

    /// <summary>Polish form of the protagonist title ("Podróżnik" for "Traveler (Hydro)"), else "".</summary>
    public static string Gloss(string englishName, Func<string, string> exact)
    {
        if (string.IsNullOrWhiteSpace(englishName) || exact is null) return "";
        int paren = englishName.IndexOf(" (", StringComparison.Ordinal);
        string title = (paren > 0 ? englishName[..paren] : englishName).Trim();
        if (Array.FindIndex(GlossedTitles, t => string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) < 0) return "";

        string local = exact(title);
        return string.IsNullOrWhiteSpace(local) || string.Equals(local.Trim(), title, StringComparison.OrdinalIgnoreCase)
            ? ""
            : local.Trim();
    }
}
