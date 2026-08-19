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
        /// <para>Zenless Zone Zero (under any of its spellings) gets the geometry
        /// classifier; null, empty and every other value get the accent classifier
        /// that Genshin and Star Rail have always used.</para>
        ///
        /// <para>Returns shared stateless instances, so this is safe to call once
        /// per OCR tick — no allocation, no lock.</para>
        /// </summary>
        public static ITextBlockClassifier Create(string game)
        {
            return Detection.GameRegionProfile.Get(game).UsesGeometryClassifier
                ? (ITextBlockClassifier)GeometryTextBlockClassifier.Instance
                : AccentColorTextBlockClassifier.Instance;
        }
    }
}
