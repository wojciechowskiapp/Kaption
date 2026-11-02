using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ManifestEntry = GI_Subtitles.Services.Translation.DictionarySyncService.ManifestEntry;

namespace GI_Test
{
    /// <summary>
    /// A revision bump that ships the same pack (7.2.0 → 7.2.0-r2) relabels the installed pack: no download, no
    /// decrypt-and-verify, no index rebuild, no restart prompt. Tier 1 compares the server's pack_sha256 with the installed
    /// pack's hash, tier 2 the very same published file (file_sha256 and size) under a new version. Without either signal
    /// the sync does what it always did.
    /// </summary>
    [TestClass]
    public class PackRelabelTests
    {
        private string _dir;
        private string _pack;

        private const string PackSha = "4f6c8e2a0b1d3c5e7f9a2b4c6d8e0f1a3b5c7d9e1f2a4b6c8d0e2f4a6b8c0d2e";
        private const string DistSha = "aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff6677889900";

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "kaption-relabel-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_dir);
            _pack = Path.Combine(_dir, "TextMapPL.gisub");
            File.WriteAllText(_pack, "machine-bound pack bytes");
        }

        [TestCleanup]
        public void Teardown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private static FileMetadata Meta(string version, string distSha, long size, string packSha = null, string plaintextSha = null, string id = null)
        {
            var row = new Dictionary<string, object>
            {
                ["id"] = id ?? "fv_" + version, ["game"] = "genshin", ["language"] = "pl", ["version"] = version,
                ["file_size"] = size, ["file_sha256"] = distSha,
            };
            if (packSha != null) row["pack_sha256"] = packSha;
            if (plaintextSha != null) row["plaintext_sha256"] = plaintextSha;
            return JsonSerializer.Deserialize<FileMetadata>(JsonSerializer.Serialize(row));
        }

        private Dictionary<string, ManifestEntry> Installed(string version = "7.2.0", string distSha = DistSha, long size = 103_809_024) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["genshin/pl"] = new ManifestEntry
            {
                Game = "genshin", Language = "pl", Version = version, FileVersionId = "fv_" + version, FileSha256 = distSha,
                LocalPath = _pack, DownloadedAtUnix = 1793500000, FileSizeBytes = size,
            },
        };

        /// <summary>Counts every side effect; the download writes a plaintext pack whose hash is <paramref name="plaintext"/>'s.</summary>
        private sealed class Spy
        {
            public int Downloads, Encrypts, Hashes, Invalidations;
            public string Plaintext = "pack plaintext";
            public string InstalledHash = PackSha;

            public DictionarySyncService.PackOps Ops(string finalPath) => new()
            {
                Download = (meta, path, progress, ct) => { Downloads++; File.WriteAllText(path, Plaintext); return Task.CompletedTask; },
                Encrypt = (plain, final) => { Encrypts++; File.Copy(plain, final, overwrite: true); },
                HashInstalledPack = _ => { Hashes++; return InstalledHash; },
                InvalidateCaches = (game, lang) => { Invalidations++; return 2; },
                FinalPath = (game, lang) => finalPath,
            };
        }

        private async Task<DictionarySyncResult> Sync(FileMetadata meta, Dictionary<string, ManifestEntry> manifest, Spy spy)
        {
            var result = new DictionarySyncResult();
            await DictionarySyncService.SyncOneCoreAsync(meta, manifest, result, spy.Ops(_pack), CancellationToken.None);
            return result;
        }

        private static void AssertRelabelled(DictionarySyncResult r, Spy spy, Dictionary<string, ManifestEntry> manifest, string version, string distSha)
        {
            Assert.AreEqual(1, r.Relabelled);
            Assert.AreEqual(0, r.Downloaded, "no download, so callers never prompt a restart");
            Assert.AreEqual(0, spy.Downloads, "nothing fetched");
            Assert.AreEqual(0, spy.Encrypts, "nothing decrypted, verified or re-encrypted");
            Assert.AreEqual(0, spy.Invalidations, "the matcher index is kept, so nothing is rebuilt");
            Assert.AreEqual(version, manifest["genshin/pl"].Version, "the installed pack carries the new version");
            Assert.AreEqual("fv_" + version, manifest["genshin/pl"].FileVersionId);
            Assert.AreEqual(distSha, manifest["genshin/pl"].FileSha256, "the next sync sees it as up to date");
        }

        private static void AssertDownloaded(DictionarySyncResult r, Spy spy)
        {
            Assert.AreEqual(1, r.Downloaded);
            Assert.AreEqual(0, r.Relabelled);
            Assert.AreEqual(1, spy.Downloads);
            Assert.AreEqual(1, spy.Encrypts);
            Assert.AreEqual(1, spy.Invalidations, "a new pack rebuilds the index, as before");
        }

        // ── Tier 1: pack_sha256 ────────────────────────────────────────────────

        [TestMethod]
        public async Task Tier1_SamePackHash_Relabels_WithoutDownloadOrRebuild()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            var manifest = Installed();
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", "re-encrypted-" + DistSha[13..], 103_809_040, packSha: PackSha), manifest, spy);
            AssertRelabelled(r, spy, manifest, "7.2.0-r2", "re-encrypted-" + DistSha[13..]);
            Assert.AreEqual(0, spy.Hashes, "the recorded hash is used, the pack is not read");
        }

        [TestMethod]
        public async Task Tier1_PlaintextShaAlias_Works_Too()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            var manifest = Installed();
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", "new-dist", 103_809_040, plaintextSha: PackSha.ToUpperInvariant()), manifest, spy);
            AssertRelabelled(r, spy, manifest, "7.2.0-r2", "new-dist");
        }

        [TestMethod]
        public async Task Tier1_DifferentPackHash_DownloadsAsToday()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", "new-dist", 103_900_000, packSha: new string('b', 64)), Installed(), spy);
            AssertDownloaded(r, spy);
        }

        [TestMethod]
        public async Task Tier1_PackInstalledBefore26_IsHashedOnce_ThenRemembered()
        {
            var manifest = Installed(); // no record next to the pack
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", "dist-r2", 103_809_040, packSha: PackSha), manifest, spy);
            AssertRelabelled(r, spy, manifest, "7.2.0-r2", "dist-r2");
            Assert.AreEqual(1, spy.Hashes, "read once from the installed pack");

            var again = new Spy();
            var r3 = await Sync(Meta("7.2.0-r3", "dist-r3", 103_809_050, packSha: PackSha), manifest, again);
            AssertRelabelled(r3, again, manifest, "7.2.0-r3", "dist-r3");
            Assert.AreEqual(0, again.Hashes, "the hash is cached next to the pack from then on");
        }

        [TestMethod]
        public async Task Tier1_UnreadableInstalledPack_Downloads()
        {
            var spy = new Spy { InstalledHash = null };
            var r = await Sync(Meta("7.2.0-r2", "dist-r2", 103_809_040, packSha: PackSha), Installed(), spy);
            AssertDownloaded(r, spy);
        }

        // ── Tier 2: the same published file under a new version ────────────────

        [TestMethod]
        public async Task Tier2_SameFileShaAndSize_Relabels()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            var manifest = Installed();
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", DistSha, 103_809_024), manifest, spy);
            AssertRelabelled(r, spy, manifest, "7.2.0-r2", DistSha);
            Assert.AreEqual(0, spy.Hashes);
        }

        [TestMethod]
        public async Task Tier2_SameShaButAnotherSize_Downloads()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", DistSha, 103_809_025), Installed(), spy);
            AssertDownloaded(r, spy);
        }

        [TestMethod]
        public async Task NoRecord_NoPackHash_DownloadsOnce_ThenSkipsTheNextIdenticalRevision()
        {
            var manifest = Installed();
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", "dist-r2", 103_809_040), manifest, spy);
            AssertDownloaded(r, spy);
            Assert.AreEqual(0, spy.Hashes, "without pack_sha256 the installed pack is never read");
            var record = PackRecord.Read(_pack);
            Assert.IsNotNull(record, "the download leaves a record next to the pack");
            Assert.AreEqual(PackRecord.Sha256OfStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(spy.Plaintext))), record.PackSha256,
                "the record holds the hash of the decrypted pack");

            var next = new Spy();
            var r3 = await Sync(Meta("7.2.0-r3", "dist-r2", 103_809_040), manifest, next);
            AssertRelabelled(r3, next, manifest, "7.2.0-r3", "dist-r2");
        }

        [TestMethod]
        public async Task SameVersionAndSha_IsUpToDate_AsBefore()
        {
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0", DistSha, 103_809_024, packSha: PackSha), Installed(), spy);
            Assert.AreEqual(1, r.UpToDate);
            Assert.AreEqual(0, spy.Downloads + spy.Hashes + spy.Invalidations);
        }

        [TestMethod]
        public async Task AReplacedPackFile_InvalidatesItsRecord()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 103_809_024);
            File.WriteAllText(_pack, "something else wrote this pack");
            File.SetLastWriteTimeUtc(_pack, DateTime.UtcNow.AddMinutes(5));
            Assert.IsNull(PackRecord.Read(_pack), "the record describes the file it was written for");
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", DistSha, 103_809_024), Installed(), spy);
            AssertDownloaded(r, spy);
        }

        [TestMethod]
        public async Task NoInstalledPack_Downloads()
        {
            File.Delete(_pack);
            var spy = new Spy();
            var r = await Sync(Meta("7.2.0-r2", DistSha, 103_809_024, packSha: PackSha), Installed(), spy);
            AssertDownloaded(r, spy);
        }

        [TestMethod]
        public void TheSidecar_IsNotMistakenForAPackOrAJsonFile()
        {
            PackRecord.Write(_pack, PackSha, DistSha, 1);
            Assert.AreEqual(1, Directory.GetFiles(_dir, "*.gisub").Length, "the inventory scan still sees one pack");
            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.json").Length);
        }
    }
}
