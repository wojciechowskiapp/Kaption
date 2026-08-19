using System;
using System.Linq;
using GI_Subtitles.Services.OCR.Classification;

namespace GI_Subtitles.Services.Translation.Strategies
{
    /// <summary>
    /// Name normalizer for Zenless Zone Zero: strips the ornament the cinematic
    /// style prints around the speaker before doing the usual trim-and-lowercase.
    ///
    /// <para><see cref="TrimNameNormalizer"/> splits on <c>{' ', ',', '.'}</c> and
    /// nothing else, so the measured recognition <c>-Remielle</c> normalizes to
    /// <c>-remielle</c> and misses every entry in the name-to-role index. The
    /// consequence is not a wrong name, it is no name at all: the reverse index
    /// lookup fails, the hot-cache preload never runs for that speaker, and role
    /// disambiguation loses its tie-breaker.</para>
    ///
    /// <para>The geometry classifier already strips decoration when it emits the
    /// name block, so in the normal path this runs on text that is clean. It stays
    /// as the second line of defence for names that reach the context engine by
    /// another route — a cached name from a previous session, a name typed into a
    /// test, a future classifier that does not strip.</para>
    /// </summary>
    public sealed class ZzzNameNormalizer : INpcNameNormalizer
    {
        /// <summary>Same split set as <see cref="TrimNameNormalizer"/> — decoration is gone by then.</summary>
        private static readonly char[] Separators = { ' ', ',', '.' };

        /// <inheritdoc/>
        public string NormalizeFull(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;
            return SpeakerNameDecoration.Strip(rawName).ToLowerInvariant();
        }

        /// <inheritdoc/>
        public string ExtractFirstName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;

            string stripped = SpeakerNameDecoration.Strip(rawName);
            if (stripped.Length == 0) return string.Empty;

            string first = stripped
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return first.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Per-game selection for <see cref="INpcNameNormalizer"/>.
    ///
    /// <para>Lets <c>GameDialogueContextFactory</c> wire the normalizer with a
    /// single call rather than knowing which type belongs to which game. It is
    /// called for <b>every</b> game on the passthrough arm, not just ZZZ,
    /// because it returns <see cref="TrimNameNormalizer"/> for the others —
    /// byte-identical to the base constructor's own default:</para>
    /// <code>
    /// return new NormalizedDialogueContext(
    ///     expectedGame: normalized,
    ///     nextResolver: null, questFmt: null,
    ///     nameNorm: NpcNameNormalizerFactory.Create(normalized),
    ///     disambig: null);
    /// </code>
    /// <para>The four-strategy constructor treats null arguments as "use the
    /// default", so only the name normalizer needs naming.</para>
    /// </summary>
    public static class NpcNameNormalizerFactory
    {
        /// <summary>
        /// Keys off <c>GameRegionProfile.StripsSpeakerNameDecoration</c> rather
        /// than a game-id comparison, so a game that needs decoration stripping
        /// declares it once in the registry. Zenless Zone Zero (under any of its
        /// registered spellings) gets <see cref="ZzzNameNormalizer"/>; null,
        /// unknown ids and every shipping game get <see cref="TrimNameNormalizer"/>.
        /// </summary>
        public static INpcNameNormalizer Create(string game)
        {
            return GI_Subtitles.Services.Detection.GameRegionProfile.Get(game).StripsSpeakerNameDecoration
                ? (INpcNameNormalizer)new ZzzNameNormalizer()
                : new TrimNameNormalizer();
        }
    }
}
