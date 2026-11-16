// ─────────────────────────────────────────────────────────────────────────────
//  CrashReportingSdkTests.cs
//  ---------------------------------------------------------------------------
//  The crash-report path with the real Sentry SDK, sending to a listener on
//  this machine (never the kaption-desktop project): an Error log line becomes
//  an event with the user's folder name scrubbed, nothing leaves without
//  consent or with an empty DSN, and only error events ever leave (no logs,
//  metrics, sessions or transactions). A major SDK release can flip a default;
//  these tests are what tells.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Logging;
using GI_Subtitles.Services.Observability;
using log4net.Repository.Hierarchy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sentry;
using AppConfig = GI_Subtitles.Core.Config.Config;
using AppLogger = GI_Subtitles.Common.Logger;

namespace GI_Test
{
    [TestClass]
    [DoNotParallelize] // one Sentry SDK, one log4net root and one Config per test process
    public class CrashReportingSdkTests
    {
        private const string ConsentKey = "CrashReportingEnabled";
        private const string DsnKey = "SentryDsn";

        private static Hierarchy Repo => (Hierarchy)log4net.LogManager.GetRepository(typeof(AppLogger).Assembly);

        private LoopbackSentry _server;
        private SentryAppender _appender;
        private bool _repoWasConfigured;
        private bool _hadConsent, _hadDsn;
        private bool _consent;
        private string _dsn;

        [TestInitialize]
        public void SetUp()
        {
            _hadConsent = AppConfig.Has(ConsentKey);
            _consent = AppConfig.Get(ConsentKey, false);
            _hadDsn = AppConfig.Has(DsnKey);
            _dsn = AppConfig.Get<string>(DsnKey, null);

            // Whatever started the SDK before this class (another test, a bootstrap) is closed first: StartSdk keeps
            // a running SDK, so its DSN would win over the local one and these Error lines would leave the machine.
            CrashReportingService.Shutdown();
            if (SentrySdk.IsEnabled) SentrySdk.Close();
            Assert.IsFalse(SentrySdk.IsEnabled, "no Sentry SDK may be running when a test starts");

            _server = new LoopbackSentry();
            CrashReportingService.TestOptions = o => o.DisableFileWrite = true;
            _repoWasConfigured = Repo.Configured;
            Repo.Configured = true;
            _appender = new SentryAppender();
            _appender.ActivateOptions();
            Repo.Root.AddAppender(_appender);
        }

        [TestCleanup]
        public void TearDown()
        {
            AppConfig.Set(ConsentKey, false);
            CrashReportingService.RefreshConsent();
            CrashReportingService.Shutdown();
            CrashReportingService.TestOptions = null;
            if (_appender != null)
            {
                Repo.Root.RemoveAppender(_appender);
                Repo.Configured = _repoWasConfigured;
            }
            Restore(ConsentKey, _hadConsent, _consent);
            Restore(DsnKey, _hadDsn, _dsn);
            _server?.Dispose();
        }

        private static void Restore<T>(string key, bool had, T value)
        {
            if (had) AppConfig.Set(key, value);
            else AppConfig.Remove(key);
        }

        /// <summary>The DSN goes in first and is read back, so consent never meets the baked production DSN.</summary>
        private void StartWithConsent(string dsn)
        {
            AppConfig.Set(DsnKey, dsn);
            Assert.AreEqual(dsn, AppConfig.Get<string>(DsnKey, null), "the DSN must be the local one before consent is given");
            AppConfig.Set(ConsentKey, true);
            CrashReportingService.Initialize();
        }

        [TestMethod]
        public void Options_KeepThePrivacySettings()
        {
            var o = new SentryOptions();
            CrashReportingService.Configure(o, _server.Dsn, "Kaption@1.2.3.4", "beta");

            Assert.AreEqual(_server.Dsn, o.Dsn);
            Assert.AreEqual("Kaption@1.2.3.4", o.Release);
            Assert.AreEqual("beta", o.Environment);
            Assert.IsFalse(o.SendDefaultPii, "no user name, machine name or IP");
            Assert.IsFalse(o.AutoSessionTracking, "sessions are usage data");
            Assert.AreEqual(0.0, o.TracesSampleRate, "no tracing");
            Assert.IsNull(o.TracesSampler, "no tracing");
            Assert.IsNull(o.ProfilesSampleRate, "no profiling");
            Assert.IsTrue(o.AttachStacktrace);
            Assert.IsFalse(o.Debug);
            Assert.AreEqual(50, o.MaxBreadcrumbs);
        }

        [TestMethod]
        public void ErrorLines_BecomeEventsWithTheUserFolderScrubbed()
        {
            StartWithConsent(_server.Dsn);
            Assert.IsTrue(CrashReportingService.IsEnabled, "consent and a DSN start the SDK");

            string id = Guid.NewGuid().ToString("N");
            string user = Environment.UserName;
            string path = $@"C:\Users\{user}\AppData\Roaming\Kaption\{id}.json";
            AppLogger.Log.Error($"Saving {path} failed ({id}-message)");
            AppLogger.Log.Error($"Reading failed ({id}-exception)", Thrown(new IOException($"Could not find file '{path}'.")));

            var events = _server.WaitFor(items => items.Count(i => i.Type == "event") >= 2).Where(i => i.Type == "event").ToList();
            Assert.AreEqual(2, events.Count, "one event per Error line");

            var message = events.Single(e => e.Payload.Contains(id + "-message"));
            var exception = events.Single(e => e.Payload.Contains(id + "-exception"));
            foreach (var e in new[] { message, exception })
            {
                using var doc = JsonDocument.Parse(e.Payload);
                var root = doc.RootElement;
                Assert.AreEqual("error", root.GetProperty("level").GetString());
                string text = e == message ? MessageText(root) : ExceptionValue(root);
                StringAssert.Contains(text, id, "the line itself is in the event");
                StringAssert.Contains(text, @"\<user>\", "the user folder is replaced");
                Assert.IsFalse(text.Contains($@"\{user}\", StringComparison.OrdinalIgnoreCase), "the user name never leaves in the text");
                // The fields the SDK fills with the machine or the person when SendDefaultPii is on.
                Assert.IsFalse(root.TryGetProperty("server_name", out _), "no server name");
                if (root.TryGetProperty("contexts", out var contexts) && contexts.TryGetProperty("device", out var device))
                    Assert.IsFalse(device.TryGetProperty("name", out _), "no device name");
                if (root.TryGetProperty("user", out var who))
                    CollectionAssert.AreEquivalent(new[] { "id" }, who.EnumerateObject().Select(p => p.Name).ToArray(), "the user is an id only");
            }
        }

        [TestMethod]
        public void NothingLeaves_WithoutConsentOrWithAnEmptyDsn()
        {
            AppConfig.Set(DsnKey, _server.Dsn);
            AppConfig.Set(ConsentKey, false);
            CrashReportingService.Initialize();
            Assert.IsFalse(CrashReportingService.IsEnabled, "no consent: the SDK stays off");
            Assert.IsFalse(SentrySdk.IsEnabled);
            AppLogger.Log.Error($"no consent {Guid.NewGuid():N}");
            CrashReportingService.ReportException(Thrown(new InvalidOperationException("no consent")), "test");

            AppConfig.Set(DsnKey, "");
            AppConfig.Set(ConsentKey, true);
            CrashReportingService.Initialize();
            Assert.IsFalse(CrashReportingService.IsEnabled, "an empty DSN keeps the SDK off");
            Assert.IsFalse(SentrySdk.IsEnabled);
            AppLogger.Log.Error($"empty dsn {Guid.NewGuid():N}");
            CrashReportingService.ReportException(Thrown(new InvalidOperationException("empty dsn")), "test");

            Thread.Sleep(1000);
            Assert.AreEqual(0, _server.RequestCount, "nothing reached the listener");
        }

        [TestMethod]
        public void OnlyErrorEventsLeave_NoLogsMetricsSessionsOrTraces()
        {
            StartWithConsent(_server.Dsn);
            Assert.IsTrue(CrashReportingService.IsEnabled);

            string id = Guid.NewGuid().ToString("N");
            SentrySdk.Logger.LogError("structured log {0}", id);
            SentrySdk.Metrics.EmitCounter("kaption.test.counter", 1);
            SentrySdk.StartTransaction("kaption.test", "test").Finish();
            AppLogger.Log.Error($"sentinel {id}");

            // Close drains every queue and batch, so whatever would leave has left once it returns.
            CrashReportingService.Shutdown();
            _server.WaitFor(all => all.Any(i => i.Type == "event" && i.Payload.Contains(id)));
            Thread.Sleep(500);
            var items = _server.Items;

            Assert.IsTrue(items.Any(i => i.Type == "event" && i.Payload.Contains(id)), "the sentinel error arrived");
            var others = items.Select(i => i.Type).Where(t => t is not ("event" or "client_report")).Distinct().ToList();
            Assert.AreEqual(0, others.Count, "only error events (and the SDK's dropped-event counts) leave: " + string.Join(", ", others));
        }

        private static Exception Thrown(Exception ex)
        {
            try { throw ex; }
            catch (Exception caught) { return caught; }
        }

        private static string MessageText(JsonElement root)
        {
            // The SDK writes a captured message as "logentry"; older payloads used "message".
            if (!root.TryGetProperty("logentry", out var m)) m = root.GetProperty("message");
            if (m.ValueKind == JsonValueKind.String) return m.GetString();
            return m.TryGetProperty("message", out var raw) ? raw.GetString()
                : m.TryGetProperty("formatted", out var formatted) ? formatted.GetString() : m.GetRawText();
        }

        private static string ExceptionValue(JsonElement root) =>
            string.Join("\n", root.GetProperty("exception").GetProperty("values").EnumerateArray()
                .Select(v => v.TryGetProperty("value", out var value) ? value.GetString() : ""));

        /// <summary>
        /// A Sentry ingest endpoint on localhost: takes envelopes, answers 200 and keeps every item it was sent.
        /// HttpListener on a localhost prefix needs no URL reservation or admin rights.
        /// </summary>
        private sealed class LoopbackSentry : IDisposable
        {
            public sealed record Item(string Type, string Payload);

            private readonly HttpListener _listener = new();
            private readonly List<Item> _items = new();
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _loop;
            private int _requests;

            public LoopbackSentry()
            {
                int port = FreePort();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                Dsn = $"http://kaptiontest@localhost:{port}/1";
                _loop = Task.Run(ServeAsync);
            }

            public string Dsn { get; }
            public int RequestCount => Volatile.Read(ref _requests);

            public IReadOnlyList<Item> Items
            {
                get { lock (_items) return _items.ToList(); }
            }

            public IReadOnlyList<Item> WaitFor(Func<IReadOnlyList<Item>, bool> done, int timeoutMs = 15000)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    var now = Items;
                    if (done(now)) return now;
                    SentrySdk.Flush(TimeSpan.FromMilliseconds(200));
                    Thread.Sleep(50);
                }
                return Items;
            }

            private async Task ServeAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening) { return; }

                    Interlocked.Increment(ref _requests);
                    try
                    {
                        byte[] body = Decode(ctx.Request.InputStream, ctx.Request.Headers["Content-Encoding"]);
                        var parsed = ParseEnvelope(body);
                        lock (_items) _items.AddRange(parsed);
                    }
                    finally
                    {
                        ctx.Response.StatusCode = 200;
                        byte[] ok = Encoding.UTF8.GetBytes("{}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.OutputStream.Write(ok, 0, ok.Length);
                        ctx.Response.Close();
                    }
                }
            }

            private static byte[] Decode(Stream input, string encoding)
            {
                using Stream decoded = encoding?.Trim().ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    _ => input,
                };
                using var ms = new MemoryStream();
                decoded.CopyTo(ms);
                return ms.ToArray();
            }

            /// <summary>Envelope: a header line, then per item a header line and a payload (length bytes, or one line).</summary>
            private static List<Item> ParseEnvelope(byte[] body)
            {
                var items = new List<Item>();
                int pos = 0;
                ReadLine(body, ref pos);
                while (pos < body.Length)
                {
                    string header = ReadLine(body, ref pos);
                    if (string.IsNullOrWhiteSpace(header)) continue;
                    using var doc = JsonDocument.Parse(header);
                    string type = doc.RootElement.GetProperty("type").GetString();
                    string payload;
                    if (doc.RootElement.TryGetProperty("length", out var length))
                    {
                        int n = length.GetInt32();
                        payload = Encoding.UTF8.GetString(body, pos, n);
                        pos += n;
                        if (pos < body.Length && body[pos] == (byte)'\n') pos++;
                    }
                    else
                    {
                        payload = ReadLine(body, ref pos);
                    }
                    items.Add(new Item(type, payload));
                }
                return items;
            }

            private static string ReadLine(byte[] body, ref int pos)
            {
                int end = Array.IndexOf(body, (byte)'\n', pos);
                if (end < 0) end = body.Length;
                string line = Encoding.UTF8.GetString(body, pos, end - pos);
                pos = Math.Min(end + 1, body.Length);
                return line;
            }

            private static int FreePort()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
                _listener.Close();
                try { _loop.Wait(2000); } catch (AggregateException) { }
                _stop.Dispose();
            }
        }
    }
}
