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

        /// <summary>What <see cref="RestoreInterruptedSave"/> found for a missing file.</summary>
        internal enum RestoreResult
        {
            /// <summary>The file is there, or no save of it was left unfinished.</summary>
            NothingToRestore,
            /// <summary>The newest unfinished save is in place now.</summary>
            Restored,
            /// <summary>An unfinished save is there but could not be read: it may be the only copy, and it is kept.</summary>
            Unreadable,
        }


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
            KeepLeftovers(full);
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
        /// <see cref="WriteAllBytes"/>), puts the newest one into place before the file is read. True when it did.
        /// </summary>
        public static bool RecoverInterruptedSave(string path) => RestoreInterruptedSave(path) == RestoreResult.Restored;

        /// <summary>
        /// <see cref="RecoverInterruptedSave"/> with the reason. A temp file that cannot be read after a few short tries
        /// (<see cref="RestoreResult.Unreadable"/>) may be the only copy left; no save deletes it (see
        /// <see cref="KeepLeftovers"/>), in this process or a later one.
        /// </summary>
        public static RestoreResult RestoreInterruptedSave(string path)
        {
            // A scanner opening a file it has just seen holds it for a moment; about half a second in all, and only here.
            for (int attempt = 1; ; attempt++)
            {
                RestoreResult result = TryRestore(path, lastAttempt: attempt == RestoreAttempts);
                if (result != RestoreResult.Unreadable || attempt == RestoreAttempts) return result;
                Thread.Sleep(100 * attempt);
            }
        }

        private const int RestoreAttempts = 3;

        private static RestoreResult TryRestore(string path, bool lastAttempt)
        {
            string full;
            FileInfo newest = null;
            try
            {
                full = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(full);
                if (File.Exists(full) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return RestoreResult.NothingToRestore;
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(full) + ".*.tmp"))
                    if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
                if (newest == null) return RestoreResult.NothingToRestore;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not look for an unfinished save of {Path.GetFileName(path)}: {ex.Message}");
                return RestoreResult.NothingToRestore;
            }
            try
            {
                File.Move(newest.FullName, full);
                Logger.Log.Warn($"{Path.GetFileName(full)} was restored from a save that did not finish.");
                KeepLeftovers(full);
                return RestoreResult.Restored;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A scanner holding the temp file stops its rename, not usually a read: write its bytes into place.
                try
                {
                    WriteAllBytes(full, File.ReadAllBytes(newest.FullName));
                    Logger.Log.Warn($"{Path.GetFileName(full)} was restored from a copy of a save that did not finish ({ex.Message}).");
                    return RestoreResult.Restored;
                }
                catch (Exception copyEx) when (copyEx is IOException || copyEx is UnauthorizedAccessException)
                {
                    if (!lastAttempt) return RestoreResult.Unreadable;
                    Logger.Log.Warn($"Could not restore {Path.GetFileName(full)} from {newest.Name}, a save that did not finish " +
                                    $"({copyEx.Message}); that file is kept and moved aside by a later save.");
                    return RestoreResult.Unreadable;
                }
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
                File.Move(path, BackupName(path));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not set the damaged {Path.GetFileName(path)} aside: {ex.Message}");
                return false;
            }
        }

        // After a save: "<name>.<id>.tmp" files are saves that never got swapped in. Any of them may be the only copy of the
        // data (a swap that failed after the target was gone, or a restore that could not read it), and nothing can tell
        // which, so none is ever deleted: each is moved aside as "<name>.unrestored-<UTC>-<id>", a name no cleanup and no
        // restore matches, or left where it is while something holds it. They exist only after a crash or a forced stop in
        // the middle of a save. File.Replace (ReplaceFileW) works through its own "<name>~RF<hex>.TMP", which a process
        // stopped in the middle leaves behind; that one holds nothing of ours and is deleted.
        private static void KeepLeftovers(string full)
        {
            try
            {
                string dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir)) return;
                foreach (string leftover in Directory.EnumerateFiles(dir, Path.GetFileName(full) + ".*.tmp"))
                    KeepUnrestored(full, leftover);
                foreach (string leftover in Directory.EnumerateFiles(dir, Path.GetFileName(full) + "~RF*.TMP")) TryDelete(leftover);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not tidy the temp files of {Path.GetFileName(full)}: {ex.Message}");
            }
        }

        private static void KeepUnrestored(string full, string leftover)
        {
            string id = Path.GetFileNameWithoutExtension(leftover);
            id = id.Substring(id.LastIndexOf('.') + 1);
            string kept = full + ".unrestored-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + id;
            try
            {
                File.Move(leftover, kept);
                Logger.Log.Warn($"The unfinished save {Path.GetFileName(leftover)} was kept as {Path.GetFileName(kept)}.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Still held: tried again at the next save, never deleted.
            }
        }

        /// <summary>
        /// Writes <paramref name="contents"/>, already read from a damaged <paramref name="path"/> that cannot be moved
        /// (something holds it), to the name <see cref="SetAside"/> would have used. The damaged file stays; the next
        /// save may replace it, the copy stays. Returns the copy's name, or null when it could not be written.
        /// </summary>
        public static string KeepCopyAside(string path, string contents)
        {
            string backup = BackupName(path);
            try
            {
                WriteAllText(backup, contents);
                return backup;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Log.Warn($"Could not keep a copy of the damaged {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private static string BackupName(string path)
        {
            string backup = path + ".bad";
            return File.Exists(backup) ? path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bad" : backup;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.Log.Warn($"Could not delete temp file {Path.GetFileName(path)}: {ex.Message}"); }
        }
    }
}
