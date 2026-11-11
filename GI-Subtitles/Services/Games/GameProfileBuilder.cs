using System;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Services.Games
{
    /// <summary>
    /// Turns a manifest into the <see cref="GameRegionProfile"/> the OCR, detection and layout code read. Values are
    /// applied onto <c>new GameRegionProfile()</c>, so a vision key the manifest leaves out keeps the property
    /// initializer (the shipping accent behaviour) and an absent pacing key stays null (the global default).
    /// </summary>
    public static class GameProfileBuilder
    {
        public static GameRegionProfile Build(GameManifest m)
        {
            GameDesktop d = m.Desktop ?? throw new ArgumentException($"{m.Id}: no desktop block", nameof(m));
            var p = new GameRegionProfile
            {
                GameId = m.GameId,
                DisplayName = m.Name,
                // No aliases is null, as the hand-written profiles had it.
                Aliases = m.Aliases is { Length: > 0 } aliases ? aliases : null,
                ProcessNames = m.Detect?.ProcessNames ?? Array.Empty<string>(),
                WindowTitles = m.Detect?.WindowTitles ?? Array.Empty<string>(),
                RequiresWindowTitle = m.Detect?.RequireTitle == true,
                DialogueXPct = d.Dialogue.X,
                DialogueYPct = d.Dialogue.Y,
                DialogueWPct = d.Dialogue.W,
                DialogueHPct = d.Dialogue.H,
                AnswerXPct = d.Answer.X,
                AnswerYPct = d.Answer.Y,
                AnswerWPct = d.Answer.W,
                AnswerHPct = d.Answer.H,
                OcrIntervalMs = d.Pacing?.OcrIntervalMs,
                StabilityWindowFrames = d.Pacing?.StabilityWindowFrames,
                StableFramesChain = d.Pacing?.StableFramesChain,
                StableFramesDefault = d.Pacing?.StableFramesDefault,
                ForceOcrAfterSeconds = d.Pacing?.ForceOcrAfterSeconds,
            };

            if (d.SessionCheck is GameSessionCheck check)
            {
                // The schema refuses a negative; here it reads as 0, which switches that sentence off.
                if (check.BlackSeconds is int black) p.CheckBlackSeconds = Math.Max(0, black);
                if (check.UnmatchedReads is int unmatched) p.CheckUnmatchedReads = Math.Max(0, unmatched);
                if (check.QuietSeconds is int quiet) p.CheckQuietSeconds = Math.Max(0, quiet);
            }

            GameVision v = d.Vision;
            if (v == null) return p;
            if (v.Classifier != null)
            {
                p.UsesGeometryClassifier = string.Equals(v.Classifier, GameVision.Geometry, StringComparison.Ordinal);
                p.UsesSpeakerRowClassifier = string.Equals(v.Classifier, GameVision.SpeakerRow, StringComparison.Ordinal);
            }
            if (v.StripsSpeakerNameDecoration is bool strip) p.StripsSpeakerNameDecoration = strip;
            if (v.SubtitlePadVertical is int pad) p.SubtitlePadVertical = pad;
            if (v.RegionSplitXFraction is double split) p.RegionSplitXFraction = split;
            if (v.RegionMinWidthFraction is double minWidth) p.RegionMinWidthFraction = minWidth;
            if (v.StrictRegionJunkFilter is bool strict) p.StrictRegionJunkFilter = strict;
            if (v.OcrJunkFilter is bool junk) p.FiltersOcrJunk = junk;
            if (v.DialogueEvidence is bool evidence) p.RequiresDialogueEvidence = evidence;
            if (v.FadedReadFilter is bool faded) p.RejectsFadedReads = faded;
            return p;
        }
    }
}
