using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Security;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The Translations banner shows one localized line per <see cref="DictionarySyncStatus"/>, never the English
    /// <see cref="DictionarySyncResult.Messages"/>. The key names are shared with the string dictionaries.
    /// </summary>
    [TestClass]
    public class DictionarySyncStatusTests
    {
        private static readonly Dictionary<DictionarySyncStatus, string> Expected = new()
        {
            [DictionarySyncStatus.NotConfigured] = "Sync_Status_NotConfigured",
            [DictionarySyncStatus.NotSignedIn] = "Sync_Status_NotSignedIn",
            [DictionarySyncStatus.KeyMissing] = "Sync_Status_KeyMissing",
            [DictionarySyncStatus.Offline] = "Sync_Status_Offline",
            [DictionarySyncStatus.NothingListed] = "Sync_Status_NothingListed",
            [DictionarySyncStatus.Failed] = "Sync_Status_Failed",
        };

        [TestMethod]
        public void EveryProblemStatus_HasItsAgreedKeyAndAFallback()
        {
            foreach (DictionarySyncStatus status in Enum.GetValues(typeof(DictionarySyncStatus)))
            {
                var notice = DictionarySyncNotice.For(status);
                if (status == DictionarySyncStatus.Ok)
                {
                    Assert.IsNull(notice, "a clean sync shows no banner");
                    continue;
                }
                Assert.IsNotNull(notice, $"{status} has no banner line");
                Assert.AreEqual(Expected[status], notice.Value.Key, $"{status} maps to the agreed key");
                Assert.IsFalse(string.IsNullOrWhiteSpace(notice.Value.Fallback), $"{status} has an English fallback");
            }
            Assert.AreEqual(Expected.Count + 1, Enum.GetValues(typeof(DictionarySyncStatus)).Length,
                "a new status needs a line here and in the string dictionaries");
        }

        [TestMethod]
        public void Fallbacks_AreDistinct_AndSayNothingAboutPlansOrPayment()
        {
            var lines = Expected.Keys.Select(s => DictionarySyncNotice.For(s).Value.Fallback).ToList();
            Assert.AreEqual(lines.Count, lines.Distinct().Count(), "each status reads differently");
            foreach (string line in lines)
            {
                // Kaption is free: no subscription wording, no jargon, no em dash (CopyRules).
                foreach (string banned in new[] { "plan", "paid", "tier", "OCR", "dictionar", "—" })
                    Assert.IsFalse(line.Contains(banned, StringComparison.OrdinalIgnoreCase), $"\"{line}\" contains \"{banned}\"");
            }
        }

        [TestMethod]
        public void Status_DefaultsToOk()
        {
            var result = new DictionarySyncResult();
            Assert.AreEqual(DictionarySyncStatus.Ok, result.Status);
            Assert.IsTrue(result.Ok);
        }

        [TestMethod]
        public async Task MissingGameOrLanguage_IsNotConfigured_WithoutTouchingTheNetwork()
        {
            var api = new KaptionApiClient();
            using var license = new LicenseService(api);
            var sync = new DictionarySyncService(api, license, new ServerKeyFileProtectionService(() => null));

            var noGame = await sync.SyncAsync("", "pl", CancellationToken.None);
            var noLanguage = await sync.SyncAsync("genshin", " ", CancellationToken.None);

            Assert.AreEqual(DictionarySyncStatus.NotConfigured, noGame.Status);
            Assert.AreEqual(DictionarySyncStatus.NotConfigured, noLanguage.Status);
            Assert.AreEqual(1, noGame.Messages.Count, "the English line stays for the log");
        }

        [TestMethod]
        public void EveryListedPackSkipped_ReadsAsNothingListed()
        {
            var allSkipped = new DictionarySyncResult { Skipped = 2 };
            allSkipped.SettleAfterDownloads();
            Assert.AreEqual(DictionarySyncStatus.NothingListed, allSkipped.Status, "a click that got nothing says why");

            var oneCurrent = new DictionarySyncResult { Skipped = 1, UpToDate = 1 };
            oneCurrent.SettleAfterDownloads();
            Assert.AreEqual(DictionarySyncStatus.Ok, oneCurrent.Status, "a skipped extra pack is not a problem");

            var failed = new DictionarySyncResult { Skipped = 1, Failed = 1, Status = DictionarySyncStatus.Failed };
            failed.SettleAfterDownloads();
            Assert.AreEqual(DictionarySyncStatus.Failed, failed.Status, "a real failure keeps its own line");

            var empty = new DictionarySyncResult();
            empty.SettleAfterDownloads();
            Assert.AreEqual(DictionarySyncStatus.Ok, empty.Status);
        }

        [TestMethod]
        public void Banner_KeepsTheDownloadLineOverTheScanThatFollows()
        {
            Assert.AreEqual("Sync_Status_NothingListed",
                DictionarySyncNotice.ForBanner(DictionarySyncStatus.NothingListed, DictionarySyncStatus.Ok)?.Key,
                "the refresh after a failed download must not collapse its line");
            Assert.AreEqual("Sync_Status_Failed",
                DictionarySyncNotice.ForBanner(DictionarySyncStatus.Failed, DictionarySyncStatus.Offline)?.Key);
            Assert.AreEqual("Sync_Status_Offline",
                DictionarySyncNotice.ForBanner(null, DictionarySyncStatus.Offline)?.Key, "no download: the scan decides");
            Assert.AreEqual("Sync_Status_Offline",
                DictionarySyncNotice.ForBanner(DictionarySyncStatus.Ok, DictionarySyncStatus.Offline)?.Key);
            Assert.IsNull(DictionarySyncNotice.ForBanner(null, DictionarySyncStatus.Ok), "all well: no banner");
        }

        // ── The mappings themselves, against a loopback server that answers like the Worker ──

        [TestMethod]
        [DataRow(401, "{}", DictionarySyncStatus.NotSignedIn)]
        [DataRow(403, "{}", DictionarySyncStatus.NothingListed)]
        [DataRow(200, "{\"items\":[]}", DictionarySyncStatus.NothingListed)]
        [DataRow(503, "{}", DictionarySyncStatus.Offline)]
        public async Task Sync_MapsTheListingAnswer(int status, string body, DictionarySyncStatus expected)
        {
            using var server = new StubApi(status, body);
            using var license = SignedIn(server.BaseUrl);
            var sync = new DictionarySyncService(new KaptionApiClient(server.BaseUrl), license,
                new ServerKeyFileProtectionService(() => null));
            try
            {
                var result = await sync.SyncAsync("genshin", "pl", CancellationToken.None);
                Assert.AreEqual(expected, result.Status);
                Assert.IsTrue(server.Paths.Count > 0 &&
                              server.Paths.All(p => p.StartsWith("/api/license/files?game=genshin&lang=pl")),
                    "only the listing was called: " + string.Join(", ", server.Paths));
            }
            finally { PackAvailability.Reset(); }
        }

        [TestMethod]
        [DataRow(401, "{}", DictionarySyncStatus.NotSignedIn)]
        [DataRow(403, "{}", DictionarySyncStatus.NothingListed)]
        [DataRow(503, "{}", DictionarySyncStatus.Offline)]
        [DataRow(200, "{\"items\":[]}", DictionarySyncStatus.Ok)]
        public async Task InventoryScan_MapsTheCatalogAnswer(int status, string body, DictionarySyncStatus expected)
        {
            using var server = new StubApi(status, body);
            using var license = SignedIn(server.BaseUrl);
            var scan = await new DictionaryInventoryService(new KaptionApiClient(server.BaseUrl), license)
                .ScanAsync(CancellationToken.None);
            Assert.AreEqual(expected, scan.RemoteStatus);
            Assert.AreEqual(expected == DictionarySyncStatus.Ok, scan.RemoteQueryOk);
        }

        [TestMethod]
        public async Task InventoryScan_WithoutASession_IsNotSignedIn()
        {
            using var license = SignedIn("http://127.0.0.1:9", jwt: null);
            var scan = await new DictionaryInventoryService(new KaptionApiClient("http://127.0.0.1:9"), license)
                .ScanAsync(CancellationToken.None);
            Assert.AreEqual(DictionarySyncStatus.NotSignedIn, scan.RemoteStatus);
        }

        // ── What players actually read: the dictionaries' text, once the strings stream has added the keys ──

        private static readonly string[] DialogKeys =
            { "Dialog_DownloadFailed_Title", "Dialog_DownloadFailed_Body", "Dialog_SignInRequired_Title", "Dialog_SignInRequired_Body" };

        [TestMethod]
        [DataRow("Strings.en-US.xaml", "plan,paid,tier,OCR,dictionar,subscription,—")]
        [DataRow("Strings.pl-PL.xaml", "abonament,subskrypc,płatn,słownik,OCR,—")]
        public void Dictionaries_ReadLikeTheFallbacks(string file, string bannedCsv)
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string path = Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", file);
            var strings = XDocument.Load(path).Root.Elements()
                .Where(e => e.Attribute(x + "Key") != null)
                .GroupBy(e => (string)e.Attribute(x + "Key"))
                .ToDictionary(g => g.Key, g => g.First().Value);
            var keys = Expected.Values.Concat(DialogKeys).ToList();
            var missing = keys.Where(k => !strings.ContainsKey(k)).ToList();
            if (missing.Count == keys.Count)
                Assert.Inconclusive($"{file} has none of the sync keys yet (the strings stream adds them).");
            Assert.AreEqual(0, missing.Count, $"{file} has some sync keys but not: {string.Join(", ", missing)}");

            foreach (string key in keys)
                foreach (string banned in bannedCsv.Split(','))
                    Assert.IsFalse(strings[key].Contains(banned, StringComparison.OrdinalIgnoreCase),
                        $"{file} {key} \"{strings[key]}\" contains \"{banned}\"");

            // FormatL passes {0}=language and {1}=game; a translation missing one would drop it.
            string body = strings["Dialog_DownloadFailed_Body"];
            Assert.IsTrue(body.Contains("{0}") && body.Contains("{1}"), $"{file} Dialog_DownloadFailed_Body: {body}");
        }

        /// <summary>
        /// A LicenseService holding a fake signed-in session. Marked disposed, so a 401 path
        /// (ReportRemoteRevocation) returns before it could clear the real activation file on a dev box.
        /// </summary>
        private static LicenseService SignedIn(string baseUrl, string jwt = "fake-jwt-for-tests")
        {
            var activation = TestProtection.FakeActivation();
            activation.DeviceSessionJwt = jwt;
            activation.DistributionKey = new byte[32];
            var license = new LicenseService(new KaptionApiClient(baseUrl));
            Field("_current").SetValue(license, activation);
            Field("_disposed").SetValue(license, true);
            return license;

            static FieldInfo Field(string name) =>
                typeof(LicenseService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(LicenseService), name);
        }

        /// <summary>Answers every request on a loopback port with one status and body, and records the paths.</summary>
        private sealed class StubApi : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly List<string> _paths = new();

            public StubApi(int status, string body)
            {
                _listener.Start();
                BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
                _ = Task.Run(() => ServeAsync(status, body));
            }

            public string BaseUrl { get; }

            public IReadOnlyList<string> Paths { get { lock (_paths) return _paths.ToList(); } }

            private async Task ServeAsync(int status, string body)
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { return; }
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var request = new StringBuilder();
                            var buffer = new byte[4096];
                            while (!request.ToString().Contains("\r\n\r\n"))
                            {
                                int read = await stream.ReadAsync(buffer, _stop.Token);
                                if (read == 0) break;
                                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }
                            string[] line = request.ToString().Split("\r\n")[0].Split(' ');
                            if (line.Length > 1) lock (_paths) _paths.Add(line[1]);
                            string head = $"HTTP/1.1 {status} Stub\r\nContent-Type: application/json\r\n" +
                                          $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                            await stream.WriteAsync(payload, _stop.Token);
                        }
                        catch (Exception) when (_stop.IsCancellationRequested) { return; }
                        catch (IOException) { /* the client hung up; serve the next one */ }
                    }
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
            }
        }
    }
}
