namespace GI_Subtitles.Services.OCR.Classification
{
    /// <summary>
    /// One-stop entry point for picking an <see cref="ITextBlockClassifier"/> for
    /// a given game, mirroring <c>GameDialogueContextFactory</c> so both
    /// per-game selections read the same way.
    ///
    /// <para>Unlike the dialogue-context factory this one is deliberately
    /// <b>permissive</b> rather than fail-closed. That factory fails closed
    /// because an unrecognised game there disables a cross-game safety gate; here
    /// an unrecognised game just means "classify the way the two shipping games
    /// are classified", which is the correct behaviour for every accent-coloured
    /// dialogue UI and a harmless no-op for anything else.</para>
    /// </summary>
    public static class TextBlockClassifierFactory
    {
        /// <summary>
        /// Resolve the classifier for <paramref name="game"/>.
        ///
        /// <para>The profile's capability flags decide: <c>UsesGeometryClassifier</c>
        /// (Zenless Zone Zero) wins, then <c>UsesSpeakerRowClassifier</c> (Wuthering
        /// Waves), else the accent classifier that Genshin and Star Rail have always
        /// used, which is also what null, empty and unknown games get.
        /// <c>FiltersOcrJunk</c> picks the junk-filtering instance of the latter two.</para>
        ///
        /// <para>Returns shared stateless instances, so this is safe to call once
        /// per OCR tick — no allocation, no lock.</para>
        /// </summary>
        public static ITextBlockClassifier Create(string game)
        {
            var profile = Detection.GameRegionProfile.Get(game);
            if (profile.UsesGeometryClassifier) return GeometryTextBlockClassifier.Instance;
            if (profile.UsesSpeakerRowClassifier)
            {
                return profile.FiltersOcrJunk
                    ? SpeakerRowTextBlockClassifier.WithJunkFilter
                    : SpeakerRowTextBlockClassifier.Instance;
            }
            return profile.FiltersOcrJunk
                ? AccentColorTextBlockClassifier.WithJunkFilter
                : AccentColorTextBlockClassifier.Instance;
        }
    }
}
