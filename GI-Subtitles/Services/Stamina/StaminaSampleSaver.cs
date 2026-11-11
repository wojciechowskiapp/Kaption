using System;
using System.Globalization;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Data;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace GI_Subtitles.Services.Stamina
{
    /// <summary>
    /// The testing aid behind <c>Stamina.SaveSamples</c>: keeps small pictures of what the reader saw in
    /// <c>%APPDATA%\Kaption\&lt;Game&gt;\stamina-samples\</c>, so a wrong or missed read can be sent in. A read or a
    /// refusal saves the pill (icon and number); a look that found no icon saves the whole strip, at most once per
    /// <see cref="MissEvery"/>. The folder holds at most the configured number of files, oldest out first. Nothing
    /// leaves the PC by itself.
    /// </summary>
    public sealed class StaminaSampleSaver
    {
        public const string FolderName = "stamina-samples";

        /// <summary>How often a strip without an icon is kept; most looks find none.</summary>
        public static readonly TimeSpan MissEvery = TimeSpan.FromMinutes(1);

        private readonly string _dir;
        private readonly int _limit;
        private DateTimeOffset _lastMiss = DateTimeOffset.MinValue;
        private StaminaSample _lastSaved;

        public StaminaSampleSaver(string gameId, int limit) : this(Path.Combine(GameDataPaths.GameDir(gameId), FolderName), limit, false) { }

        internal StaminaSampleSaver(string dir, int limit, bool _)
        {
            _dir = dir;
            _limit = Math.Max(1, limit);
        }

        public string Folder => _dir;

        /// <summary>Saves what is worth keeping from one look; returns the file written, or null.</summary>
        public string Save(Mat strip, StaminaSample sample, DateTimeOffset now)
        {
            if (strip == null || strip.Empty()) return null;
            Mat picture = null;
            try
            {
                string tag;
                switch (sample.Outcome)
                {
                    case StaminaSampleOutcome.NoIcon:
                        if (now - _lastMiss < MissEvery) return null;
                        _lastMiss = now;
                        picture = strip;
                        tag = "no-icon";
                        break;
                    case StaminaSampleOutcome.ReaderBusy:
                        return null;
                    default:
                        // The same number as the last saved one, read from an unchanged picture: nothing new to keep.
                        if (sample.Reused && _lastSaved.Outcome == sample.Outcome && _lastSaved.Value == sample.Value) return null;
                        picture = sample.Line.Width > 0 ? new Mat(strip, Around(sample.Line, strip)) : strip;
                        tag = sample.Outcome == StaminaSampleOutcome.Read
                            ? "read-" + sample.Value.ToString(CultureInfo.InvariantCulture)
                            : sample.Outcome == StaminaSampleOutcome.Refused ? "refused" : "no-number";
                        break;
                }
                _lastSaved = sample;
                Directory.CreateDirectory(_dir);
                // UTC in the name: the folder is pruned by name, and local time repeats an hour when the clocks go back.
                string file = Path.Combine(_dir, now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "Z-" + tag + ".png");
                if (!Cv2.ImEncode(".png", picture, out byte[] png)) return null;
                File.WriteAllBytes(file, png);
                Prune();
                return file;
            }
            finally
            {
                if (picture != null && !ReferenceEquals(picture, strip)) picture.Dispose();
            }
        }

        /// <summary>The number's box widened to take in the icon on its left and a margin, inside the strip.</summary>
        private static CvRect Around(CvRect line, Mat strip)
        {
            int pad = line.Height;
            return new CvRect(line.X - pad * 3, line.Y - pad / 2, line.Width + pad * 5, line.Height + pad)
                & new CvRect(0, 0, strip.Width, strip.Height);
        }

        private void Prune()
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(_limit);
            foreach (FileInfo old in files)
            {
                try { old.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
