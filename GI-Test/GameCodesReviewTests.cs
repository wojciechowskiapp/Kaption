// Fixes for the sol review of PR #33: a refresh asked for during a read (S1), the body cap before buffering (S2),
// and the card's own clock for expiry and staleness (S3).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Codes;
using GI_Subtitles.Services.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CodesPollerRefreshTests
    {
        // S1: a game switch while a read was in flight used to be dropped (the busy tick returned Skipped and rearmed the
        // normal interval), leaving the new game's card empty for up to 30 minutes.
        [TestMethod]
        public async Task ARefreshAskedForDuringAReadRunsRightAfterItForTheNewestGame()
        {
            var games = new ConcurrentQueue<string>();
            var first = new TaskCompletionSource<RedeemCodeList>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string selected = "genshin";
            Task<RedeemCodeList> Fetch(string game, CancellationToken ct)
            {
                games.Enqueue(game);
                if (game == "genshin") return first.Task;
                second.TrySetResult(true);
                return Task.FromResult(CodesSamples.List(game, 1));
            }
            var slow = new CodesSchedule(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), CodesSchedule.Default.Retry);
            using var poller = new CodesPoller(Fetch, () => Volatile.Read(ref selected), () => true, () => slow, () => CodesSamples.Now);
            poller.Start();
            Assert.IsTrue(poller.RequestRefresh(), "nothing cached: read now");
            await WaitUntil(() => poller.IsReading, "the genshin read started");

            Volatile.Write(ref selected, "starrail");
            Assert.IsTrue(poller.RequestRefresh(), "busy: remembered, not dropped");
            first.SetResult(CodesSamples.List("genshin", 2));

            Assert.AreSame(second.Task, await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromSeconds(10))), "the star rail read followed");
            await WaitUntil(() => poller.Last("starrail") != null, "the star rail list is kept");
            CollectionAssert.AreEqual(new[] { "genshin", "starrail" }, games.ToArray());
            poller.Stop();
        }

        [TestMethod]
        public async Task ARememberedRefreshStillRespectsTheBackoff()
        {
            var games = new ConcurrentQueue<string>();
            var first = new TaskCompletionSource<RedeemCodeList>(TaskCreationOptions.RunContinuationsAsynchronously);
            string selected = "genshin";
            Task<RedeemCodeList> Fetch(string game, CancellationToken ct)
            {
                games.Enqueue(game);
                return game == "genshin" ? first.Task : Task.FromResult(CodesSamples.List(game, 1));
            }
            var slow = new CodesSchedule(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), CodesSchedule.Default.Retry);
            using var poller = new CodesPoller(Fetch, () => Volatile.Read(ref selected), () => true, () => slow, () => CodesSamples.Now);
            poller.Start();
            poller.RequestRefresh();
            await WaitUntil(() => poller.IsReading, "the genshin read started");
            Volatile.Write(ref selected, "starrail");
            poller.RequestRefresh();
            first.SetException(new RateLimitedException(TimeSpan.FromHours(2)));
            await WaitUntil(() => !poller.IsReading && poller.Failures == 1, "the genshin read failed");
            await Task.Delay(300);
            CollectionAssert.AreEqual(new[] { "genshin" }, games.ToArray(), "the server asked to wait: the remembered read waits too");
            poller.Stop();
        }

        internal static async Task WaitUntil(Func<bool> condition, string what)
        {
            for (int i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
            Assert.IsTrue(condition(), what);
        }
    }

    [TestClass]
    public class CodesBodyLimitTests
    {
        /// <summary>A body with no Content-Length that never ends: what a misbehaving endpoint could send chunked.</summary>
        private sealed class EndlessStream : Stream
        {
            public long Served;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Served; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Array.Fill(buffer, (byte)'a', offset, count);
                Served += count;
                return count;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // S2: the cap used to be checked after ReadAsStringAsync had buffered and decoded the whole body.
        [TestMethod]
        public async Task AnOversizedBodyWithoutALengthIsRefusedBeforeItIsBuffered()
        {
            var endless = new EndlessStream();
            using var content = new StreamContent(endless);
            Assert.IsNull(content.Headers.ContentLength, "no Content-Length, like a chunked answer");
            await Assert.ThrowsExactlyAsync<CodesFormatException>(() => KaptionApiClient.ReadCappedAsync(content, 1024, CancellationToken.None));
            Assert.IsTrue(endless.Served <= 1024 + KaptionApiClient.CodesReadChunkBytes, $"read {endless.Served} bytes against a 1 KB cap");
        }

        [TestMethod]
        public async Task ABodyWithinTheCapIsReadAsUtf8()
        {
            const string json = "{\"game\":\"genshin\",\"name\":\"Zażółć gęślą jaźń\"}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            Assert.AreEqual(json, await KaptionApiClient.ReadCappedAsync(content, 1024, CancellationToken.None));
        }

        [TestMethod]
        public async Task ADeclaredLengthPastTheCapIsRefusedWithoutReading()
        {
            var endless = new EndlessStream();
            using var content = new StreamContent(endless);
            content.Headers.ContentLength = 4096;
            await Assert.ThrowsExactlyAsync<CodesFormatException>(() => KaptionApiClient.ReadCappedAsync(content, 1024, CancellationToken.None));
            Assert.AreEqual(0, endless.Served);
        }
    }

    // S3: the card used to change only on a read; with a 6 h Retry-After a stale list or an expired code stayed up.
    [TestClass]
    public class CodesCardDeadlineTests
    {
        private static readonly TimeSpan Stale = CodesSchedule.Default.StaleAfter;

        [TestMethod]
        public void TheCardChangesNextWhenItsListGoesStale()
        {
            var game = CodesSamples.Game("genshin");
            DateTimeOffset readAt = CodesSamples.Now.AddMinutes(-30);
            var list = CodesSamples.List("genshin", 2);
            DateTimeOffset? next = CodesCardModel.NextChangeAt(game, list, readAt, CodesSamples.Now, Stale);
            Assert.AreEqual(readAt + Stale, next, "stale an hour after the read, whatever the backoff says");
            Assert.IsTrue(CodesCardModel.Build(game, list, readAt, next.Value, Stale, "pl-PL", null).Visible, "still shown at the deadline itself");
            Assert.AreEqual(CodesCardHidden.Stale,
                CodesCardModel.Build(game, list, readAt, next.Value + CodesCardModel.DeadlineSlack, Stale, "pl-PL", null).HiddenBecause,
                "hidden once the timer fires, a moment after the deadline");
        }

        [TestMethod]
        public void TheCardChangesNextWhenItsFirstCodeExpires()
        {
            var game = CodesSamples.Game("starrail");
            DateTimeOffset readAt = CodesSamples.Now.AddMinutes(-5);
            DateTimeOffset expires = CodesSamples.Now.AddMinutes(10);
            var list = new RedeemCodeList("starrail", readAt, new[]
            {
                new RedeemCode("LONGLIVED", Array.Empty<CodeReward>(), readAt, null),
                new RedeemCode("SOONGONE", Array.Empty<CodeReward>(), readAt, expires),
                new RedeemCode("ALREADYGONE", Array.Empty<CodeReward>(), readAt, CodesSamples.Now.AddMinutes(-1)),
            });
            // The code ends soon, so its "Ends in 10 min" mark counts down: the card redraws each minute until the expiry
            // itself, which comes before the stale deadline.
            DateTimeOffset at = CodesSamples.Now;
            for (int minute = 1; minute <= 10; minute++)
            {
                at = CodesCardModel.NextChangeAt(game, list, readAt, at, Stale).Value;
                Assert.AreEqual(CodesSamples.Now.AddMinutes(minute), at, $"redraw {minute}");
            }
            Assert.AreEqual(expires, at, "the last redraw is the expiry, before the stale deadline");
            var after = CodesCardModel.Build(game, list, readAt, expires, Stale, "en-US", null);
            CollectionAssert.AreEqual(new[] { "LONGLIVED" }, after.Rows.Select(r => r.Code).ToArray());
        }

        [TestMethod]
        public void AHiddenCardHasNothingToWaitFor()
        {
            var game = CodesSamples.Game("zzz");
            Assert.IsNull(CodesCardModel.NextChangeAt(game, null, null, CodesSamples.Now, Stale));
            Assert.IsNull(CodesCardModel.NextChangeAt(game, CodesSamples.List("zzz", 0), CodesSamples.Now, CodesSamples.Now, Stale));
            Assert.IsNull(CodesCardModel.NextChangeAt(game, CodesSamples.List("zzz", 2), CodesSamples.Now - Stale - TimeSpan.FromMinutes(1),
                CodesSamples.Now, Stale));
        }

        [TestMethod]
        public void TheTimerWaitIsTheDeadlinePlusSlackAndNeverNegative()
        {
            DateTimeOffset now = CodesSamples.Now;
            Assert.AreEqual(TimeSpan.FromMinutes(10) + CodesCardModel.DeadlineSlack, CodesCardModel.WaitUntil(now.AddMinutes(10), now));
            Assert.AreEqual(CodesCardModel.DeadlineSlack, CodesCardModel.WaitUntil(now.AddMinutes(-3), now));
        }
    }
}
