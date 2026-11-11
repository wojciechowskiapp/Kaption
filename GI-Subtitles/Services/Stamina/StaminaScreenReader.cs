using System;
using GI_Subtitles.Services.Games;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>Reads one cut-out text line; false when the reader is busy (the sample is skipped, never queued).</summary>
    public delegate bool StaminaLineReader(Mat line, out string text, out float score);

    public enum StaminaSampleOutcome
    {
        /// <summary>The icon is not on screen: the usual case.</summary>
        NoIcon,
        /// <summary>An icon, but no number beside it (a look-alike, or the screen is still fading in).</summary>
        NoNumber,
        /// <summary>The text reader was busy with dialogue.</summary>
        ReaderBusy,
        /// <summary>Text was read but it is not "value/cap" with the game's cap, or the read was not confident.</summary>
        Refused,
        Read,
    }

    /// <summary>One look at the screen. <see cref="Line"/> is the number's box in the strip (empty when none was cut).</summary>
    public readonly record struct StaminaSample(StaminaSampleOutcome Outcome, int Value, double IconScore, float TextScore,
        CvRect Line, string Text, bool Reused);

    /// <summary>
    /// Frames in, a stamina value or nothing out: finds the icon (<see cref="StaminaLocator"/>), cuts the number beside
    /// it, has it read and checks it (<see cref="StaminaTextParser"/>). A number whose pixels did not change since the
    /// last read is not read again, so a map left open costs one recognition. Whether a value is believed is the
    /// tracker's decision (<see cref="StaminaTracker"/>), not this class's.
    /// </summary>
    public sealed class StaminaScreenReader : IDisposable
    {
        /// <summary>Two cut-outs are the same picture when no pixel differs by more than this (0-255): capture noise, not a digit.</summary>
        private const double SamePictureTolerance = 12;

        private readonly GameStamina _stamina;
        private readonly GameStaminaScreen _spec;
        private readonly StaminaLocator _locator;
        private Mat _lastLine;
        private StaminaSample _lastResult;

        public StaminaScreenReader(GameStamina stamina, GameStaminaScreen spec, StaminaLocator locator)
        {
            _stamina = stamina ?? throw new ArgumentNullException(nameof(stamina));
            _spec = spec ?? throw new ArgumentNullException(nameof(spec));
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        }

        /// <summary>The reader for a game, or null when its manifest has no stamina, no measured screen or no icon.</summary>
        public static StaminaScreenReader ForGame(GameManifest game)
        {
            if (game?.Stamina == null || game.Desktop?.StaminaScreen == null) return null;
            StaminaLocator locator = StaminaLocator.ForSpec(game.Desktop.StaminaScreen);
            return locator == null ? null : new StaminaScreenReader(game.Stamina, game.Desktop.StaminaScreen, locator);
        }

        /// <param name="strip">The search area's pixels (<see cref="StaminaLocator.SearchArea"/>), BGR or BGRA.</param>
        /// <param name="client">The game's client size in pixels.</param>
        public StaminaSample Read(Mat strip, System.Drawing.Size client, StaminaLineReader read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            var anchors = _locator.FindIcons(strip, client);
            if (anchors.Count == 0) return Forget(new StaminaSample(StaminaSampleOutcome.NoIcon, 0, 0, 0, default, null, false));

            var miss = new StaminaSample(StaminaSampleOutcome.NoNumber, 0, anchors[0].Score, 0, default, null, false);
            foreach (StaminaAnchor anchor in anchors)
            {
                if (!_locator.TryCutNumber(strip, anchor, out CvRect box)) continue;
                using var line = new Mat(strip, box);
                if (SameAsLast(line)) return _lastResult with { Reused = true, Line = box, IconScore = anchor.Score };

                if (!read(line, out string text, out float score))
                    return new StaminaSample(StaminaSampleOutcome.ReaderBusy, 0, anchor.Score, 0, box, null, false);
                if (score >= _spec.MinTextScore && StaminaTextParser.TryParse(text, _stamina, out int value))
                    return Remember(line, new StaminaSample(StaminaSampleOutcome.Read, value, anchor.Score, score, box, text, false));
                miss = new StaminaSample(StaminaSampleOutcome.Refused, 0, anchor.Score, score, box, text, false);
            }
            return Forget(miss);
        }

        private bool SameAsLast(Mat line)
        {
            if (_lastLine == null || _lastLine.Size() != line.Size() || _lastLine.Type() != line.Type()) return false;
            using var diff = new Mat();
            Cv2.Absdiff(line, _lastLine, diff);
            using Mat flat = diff.Reshape(1);
            Cv2.MinMaxLoc(flat, out _, out double worst);
            return worst <= SamePictureTolerance;
        }

        private StaminaSample Remember(Mat line, StaminaSample result)
        {
            _lastLine?.Dispose();
            _lastLine = line.Clone();
            _lastResult = result;
            return result;
        }

        private StaminaSample Forget(StaminaSample result)
        {
            _lastLine?.Dispose();
            _lastLine = null;
            return result;
        }

        public void Dispose()
        {
            _lastLine?.Dispose();
            _lastLine = null;
            _locator.Dispose();
        }
    }
}
