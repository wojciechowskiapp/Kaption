using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// What is installed at a pack path, kept in a small file next to it (TextMapPL.gisub.packinfo): the hash of the
    /// decrypted pack, and the published file it came from (its file_sha256 and size). The record describes one exact
    /// local file (its size and write time are part of it), so a pack replaced by anything else invalidates it.
    /// <para>Lets a revision bump that ships the same pack be relabelled instead of downloaded: the server's
    /// <c>pack_sha256</c> against <see cref="PackSha256"/> (tier 1), or, while servers do not send it, the same published
    /// file under a new version (tier 2).</para>
    /// </summary>
    public sealed class PackRecord
    {
        public const string Suffix = ".packinfo";

        /// <summary>SHA-256 (hex) of the decrypted pack bytes, or null when not known yet.</summary>
        public string PackSha256 { get; set; }
        /// <summary>The manifest's file_sha256 of the dist file this pack was installed from.</summary>
        public string DistSha256 { get; set; }
        public long DistSize { get; set; }
        /// <summary>The local file this record describes.</summary>
        public long LocalSize { get; set; }
        public long LocalWriteUtcTicks { get; set; }

        public static string PathFor(string packPath) => packPath + Suffix;

        /// <summary>The record for <paramref name="packPath"/>, or null when there is none or it describes another file.</summary>
        public static PackRecord Read(string packPath)
        {
            try
            {
                string path = PathFor(packPath);
                if (string.IsNullOrEmpty(packPath) || !File.Exists(path) || !File.Exists(packPath)) return null;
                var record = JsonSerializer.Deserialize<PackRecord>(File.ReadAllText(path), JsonDefaults.Options);
                var info = new FileInfo(packPath);
                if (record == null || record.LocalSize != info.Length || record.LocalWriteUtcTicks != info.LastWriteTimeUtc.Ticks)
                    return null;
                return record;
            }
            catch (Exception ex)
            {
                Logger.Log.Info($"PackRecord: {packPath}{Suffix} unreadable ({ex.Message}); treating the pack as unrecorded.");
                return null;
            }
        }

        /// <summary>Writes the record for the file now at <paramref name="packPath"/>.</summary>
        public static void Write(string packPath, string packSha256, string distSha256, long distSize)
        {
            try
            {
                var info = new FileInfo(packPath);
                if (!info.Exists) return;
                var record = new PackRecord
                {
                    PackSha256 = packSha256,
                    DistSha256 = distSha256,
                    DistSize = distSize,
                    LocalSize = info.Length,
                    LocalWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                };
                string path = PathFor(packPath);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, JsonDefaults.Indented));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Without the record the next revision downloads as before: never worse than today.
                Logger.Log.Warn($"PackRecord: could not write {packPath}{Suffix}: {ex.Message}");
            }
        }

        public static string Sha256OfFile(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        public static string Sha256OfStream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>What to do with one pack the server lists.</summary>
    public enum PackSyncAction
    {
        UpToDate,
        /// <summary>Same content under a new version: rewrite the version, keep the file, no download, no index rebuild.</summary>
        Relabel,
        Download,
    }

    /// <summary>The decision, pure: the manifest row, the record next to the pack and the server's row.</summary>
    public static class PackSyncDecision
    {
        public static (PackSyncAction Action, string Reason) Decide(
            DictionarySyncService.ManifestEntry existing, bool localExists, FileMetadata meta, PackRecord record, Func<string> localPackSha)
        {
            if (existing == null) return (PackSyncAction.Download, "new");
            if (!localExists) return (PackSyncAction.Download, "cache-missing");

            bool sameVersion = string.Equals(existing.Version, meta.Version, StringComparison.Ordinal);
            bool shaMatches = !string.IsNullOrEmpty(existing.FileSha256)
                && string.Equals(existing.FileSha256, meta.Sha256, StringComparison.OrdinalIgnoreCase);
            if (sameVersion && shaMatches) return (PackSyncAction.UpToDate, "up-to-date");

            // Tier 1: the server says what the pack holds.
            string serverPack = meta.ContentSha256;
            if (!string.IsNullOrEmpty(serverPack))
            {
                string local = record?.PackSha256;
                if (string.IsNullOrEmpty(local)) local = localPackSha?.Invoke();
                if (!string.IsNullOrEmpty(local) && string.Equals(local, serverPack, StringComparison.OrdinalIgnoreCase))
                    return (PackSyncAction.Relabel, "pack_sha256 matches the installed pack");
                return (PackSyncAction.Download, string.IsNullOrEmpty(local) ? "installed pack unreadable" : "content changed (pack_sha256 differs)");
            }

            // Tier 2: the same published file under a new version (the updater reuses the live object when nothing changed).
            if (record != null && !string.IsNullOrEmpty(record.DistSha256)
                && string.Equals(record.DistSha256, meta.Sha256, StringComparison.OrdinalIgnoreCase) && record.DistSize == meta.Size)
                return (PackSyncAction.Relabel, "same published file (file_sha256 and size)");

            string reason = !sameVersion ? $"version {existing.Version}→{meta.Version}"
                : string.IsNullOrEmpty(existing.FileSha256) ? "sha-unrecorded (legacy manifest)" : "content-changed (sha differs)";
            return (PackSyncAction.Download, reason);
        }
    }
}
