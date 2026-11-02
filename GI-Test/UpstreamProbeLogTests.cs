using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    // GlitchTip 8662879 / 8292628: a failed upstream repo-date probe must log one Warn line,
    // with type, status and host, never a stack trace or the full URL.
    [TestClass]
    public class UpstreamProbeLogTests
    {
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

        [TestMethod]
        public void Describe_HttpRequestExceptionWithStatus_IncludesStatusCode()
        {
            var ex = new HttpRequestException(
                "Response status code does not indicate success: 522 (<none>).", null, (HttpStatusCode)522);

            string text = UpstreamProbeLog.Describe(ex, ProbeTimeout);

            StringAssert.StartsWith(text, "HttpRequestException (HTTP 522): ");
            StringAssert.Contains(text, "does not indicate success");
        }

        [TestMethod]
        public void Describe_ProbeTokenCancellation_ReportsTheProbeTimeout()
        {
            var ex = new TaskCanceledException("The operation was canceled.");

            Assert.AreEqual("TaskCanceledException: timed out after 10 s", UpstreamProbeLog.Describe(ex, ProbeTimeout));
        }

        [TestMethod]
        public void Describe_HttpClientTimeout_KeepsItsOwnDuration()
        {
            var ex = new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException("The operation was canceled."));

            Assert.AreEqual(
                "TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                UpstreamProbeLog.Describe(ex, ProbeTimeout));
        }

        [TestMethod]
        [DataRow("https://gitlab.com/Dimbreath/turnbasedgamedata/-/refs/main/logs_tree/?format=json&offset=0&ref_type=HEADS", "gitlab.com")]
        [DataRow("https://git.mero.moe/dimbreath/ZenlessData", "git.mero.moe")]
        [DataRow("https://user:secret@example.com/feed.atom?token=abc", "example.com")]
        [DataRow("not a url", "(unknown host)")]
        public void Host_KeepsOnlyTheHost(string url, string expected)
        {
            Assert.AreEqual(expected, UpstreamProbeLog.Host(url));
        }

        [TestMethod]
        public void ProbeFailed_IsOneLineWithHostAndNoQuery()
        {
            const string url = "https://gitlab.com/Dimbreath/turnbasedgamedata/-/refs/main/logs_tree/?format=json&offset=0";
            var ex = new InvalidOperationException("first line\r\nsecond line\nthird line");

            string line = UpstreamProbeLog.ProbeFailed(url, ex, ProbeTimeout);

            Assert.AreEqual(
                "Upstream repo date check failed for gitlab.com: InvalidOperationException: first line second line third line",
                line);
        }
    }
}
