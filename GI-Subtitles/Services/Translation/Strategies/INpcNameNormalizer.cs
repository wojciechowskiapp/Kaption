using System;
using System.Linq;

namespace GI_Subtitles.Services.Translation.Strategies
{
    /// <summary>
    /// Strategy: normalize raw OCR-detected NPC names for both the reverse
    /// name-to-role index (built at load time) and runtime preload lookups.
    /// Kept separate from the role disambiguator because it's pure text
    /// munging with no graph dependency.
    /// </summary>
    public interface INpcNameNormalizer
    {
        /// <summary>Produce a casing-normalized full name for index keys.</summary>
        string NormalizeFull(string rawName);

        /// <summary>Extract the first-name token — the matching unit most
        /// robust to OCR noise on trailing titles/roles.</summary>
        string ExtractFirstName(string rawName);
    }

    /// <summary>Default: trim + split on whitespace/,./ then lowercase.
    /// Matches the ad-hoc logic the pre-refactor engine embedded inline.</summary>
    public sealed class TrimNameNormalizer : INpcNameNormalizer
    {
        private static readonly char[] Separators = { ' ', ',', '.' };

        public string NormalizeFull(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;
            return rawName.Trim().ToLowerInvariant();
        }

        public string ExtractFirstName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;
            string first = rawName
                .Trim()
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return first.ToLowerInvariant();
        }
    }
}
