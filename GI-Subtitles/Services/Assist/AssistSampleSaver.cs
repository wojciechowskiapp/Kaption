using System;
using System.Globalization;
using System.IO;
using System.Linq;
using GI_Subtitles.Services.Data;
using OpenCvSharp;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>
    /// The testing aid behind the Diagnostics switch: small pictures of what a reader or a screen anchor saw, kept in
    /// one folder that holds at most a number of files and a number of bytes, oldest out first. A reader hands in its
    /// own area or a cut of it, never a whole frame and never an account number. Nothing leaves the PC by itself.
    /// </summary>
    public sealed class AssistSampleSaver
    {
        /// <summary>Under the app's data root: one folder per game for what has no folder of its own.</summary>
        public const string FolderName = "assist-samples";

        private readonly string _dir;
        private readonly int _maxFiles;
        private readonly long _maxBytes;

        public AssistSampleSaver(string dir, int maxFiles, long maxBytes)
        {
            _dir = dir ?? throw new ArgumentNullException(nameof(dir));
            _maxFiles = Math.Max(1, maxFiles);
            _maxBytes = Math.Max(1, maxBytes);
        }

        /// <summary>The folder for a game's anchor samples: <c>%APPDATA%\Kaption\assist-samples\&lt;game&gt;\</c>.</summary>
        public static string FolderFor(string gameId) => Path.Combine(GameDataPaths.Root, FolderName, Path.GetFileName(GameDataPaths.GameDir(gameId)));

        public string Folder => _dir;

        /// <summary>Writes one picture as <c>&lt;UTC time&gt;Z-&lt;tag&gt;.png</c> and prunes the folder; the file, or null.</summary>
        public string Save(Mat picture, string tag, DateTimeOffset now)
        {
            if (picture == null || picture.Empty() || string.IsNullOrEmpty(tag)) return null;
            if (!Cv2.ImEncode(".png", picture, out byte[] png)) return null;
            // A picture heavier than the whole folder may be is not kept: the cap is a bound, not a wish.
            if (png.Length > _maxBytes) return null;
            Directory.CreateDirectory(_dir);
            // UTC in the name: the folder is pruned by name, and local time repeats an hour when the clocks go back.
            string file = Path.Combine(_dir, now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "Z-" + tag + ".png");
            File.WriteAllBytes(file, png);
            Prune();
            return file;
        }

        private void Prune()
        {
            FileInfo[] files = new DirectoryInfo(_dir).GetFiles("*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
            long bytes = 0;
            for (int i = 0; i < files.Length; i++)
            {
                bytes += files[i].Length;
                if (i < _maxFiles && bytes <= _maxBytes) continue;
                try { files[i].Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Removes a sample folder and what is in it. True when nothing is left (a folder that was never there
        /// counts); false when a file is held open or may not be deleted, so the caller can say so.
        /// </summary>
        public static bool DeleteFolder(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
