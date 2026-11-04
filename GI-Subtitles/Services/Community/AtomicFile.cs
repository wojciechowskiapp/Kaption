using System;
using System.IO;
using System.Text;
using System.Threading;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    /// <summary>
    /// Small-file persistence for the community layers: write a temp file next to the target, flush it to disk,
    /// then swap it in with <see cref="File.Replace(string, string, string)"/>, so a crash leaves the old or the new
    /// file and never half of one.
    /// </summary>
    internal static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string contents) =>
            WriteAllBytes(path, Utf8NoBom.GetBytes(contents ?? string.Empty));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required", nameof(path));
            bytes ??= Array.Empty<byte>();
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            try
            {
                Swap(temp, full);
            }
            catch
            {
                // A failed replace can take the target away (ERROR_UNABLE_TO_MOVE_REPLACEMENT): the temp file is then
                // the only copy, and the next read moves it into place.
                if (File.Exists(full)) TryDelete(temp);
                else Logger.Log.Warn($"{Path.GetFileName(full)} was not saved; the new copy is kept as {Path.GetFileName(temp)}.");
                throw;
            }
            DeleteLeftovers(full);
        }

        // A scanner or indexer can hold the target for a moment; a short retry covers that.
        private static void Swap(string temp, string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    else File.Move(temp, target, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 3 && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(40 * attempt);
                }
            }
        }

        /// <summary>
        /// When <paramref name="path"/> is missing but a save left its new copy in a temp file (see
        /// <see cref="WriteAllBytes"/>), moves the newest one into place before the file is read. True when it did.
        /// </summary>
        public static bool RecoverInterruptedSave(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                FileInfo newest = null;
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(full) + ".*.tmp"))
                    if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
                if (newest == null) return false;
                File.Move(newest.FullName, full);
                Logger.Log.Warn($"{Path.GetFileName(full)} was restored from a save that did not finish.");
                DeleteLeftovers(full);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not restore {Path.GetFileName(path)} from a save that did not finish: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Moves a damaged file aside as <c>&lt;name&gt;.bad</c> (a later one gets a timestamp): nothing is lost, and the
        /// next load finds no file instead of the same damage. False when it could not be moved now.
        /// </summary>
        public static bool SetAside(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                string backup = path + ".bad";
                if (File.Exists(backup)) backup = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bad";
                File.Move(path, backup);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not set the damaged {Path.GetFileName(path)} aside: {ex.Message}");
                return false;
            }
        }

        // Temp files of earlier saves that never finished; the file just written is newer than any of them.
        private static void DeleteLeftovers(string full)
        {
            try
            {
                string dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir)) return;
                foreach (string leftover in Directory.EnumerateFiles(dir, Path.GetFileName(full) + ".*.tmp")) TryDelete(leftover);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not clear old temp files of {Path.GetFileName(full)}: {ex.Message}");
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.Log.Warn($"Could not delete temp file {Path.GetFileName(path)}: {ex.Message}"); }
        }
    }
}
