using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Services.Network;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Community
{
    public enum SendOutcome
    {
        Sent,
        /// <summary>Offline, server trouble, not signed in, or the route is not live yet: try again later.</summary>
        RetryLater,
        /// <summary>The server refused the content itself; sending it again cannot help.</summary>
        Rejected,
    }

    public readonly struct SendResult
    {
        public SendResult(SendOutcome outcome, string error = null, TimeSpan? notBefore = null)
        {
            Outcome = outcome;
            Error = error ?? "";
            NotBefore = notBefore;
        }

        public SendOutcome Outcome { get; }
        public string Error { get; }
        public TimeSpan? NotBefore { get; }

        public static SendResult Sent => new SendResult(SendOutcome.Sent);
    }

    public interface ISuggestionSender
    {
        Task<SendResult> SendAsync(PendingSuggestion suggestion, CancellationToken ct);
    }

    /// <summary>Sends through <see cref="ICommunityApi"/> with the current device session.</summary>
    public sealed class ApiSuggestionSender : ISuggestionSender
    {
        private readonly ICommunityApi _api;
        private readonly Func<string> _session;

        public ApiSuggestionSender(ICommunityApi api, Func<string> session)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public async Task<SendResult> SendAsync(PendingSuggestion suggestion, CancellationToken ct)
        {
            string jwt = _session();
            if (string.IsNullOrEmpty(jwt)) return new SendResult(SendOutcome.RetryLater, "not signed in");
            try
            {
                await _api.SubmitSuggestionAsync(jwt, CommunitySuggestionRequest.From(suggestion), ct).ConfigureAwait(false);
                return SendResult.Sent;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Classify(ex);
            }
        }

        /// <summary>
        /// 409 means the server already has it. 400, 403, 413 and 422 refuse the content or the account, so the item
        /// is dropped. 401, 404 (route not deployed yet), 408, 429, 5xx and network errors retry later.
        /// </summary>
        public static SendResult Classify(Exception ex)
        {
            switch (ex)
            {
                case ApiValidationException v:
                    int code = (int)v.StatusCode;
                    if (code == (int)HttpStatusCode.Conflict) return SendResult.Sent;
                    if (code == 429) return new SendResult(SendOutcome.RetryLater, "HTTP 429", TimeSpan.FromMinutes(15));
                    if (code == 400 || code == 413 || code == 422) return new SendResult(SendOutcome.Rejected, $"HTTP {code}: {v.Message}");
                    return new SendResult(SendOutcome.RetryLater, $"HTTP {code}");
                case ForbiddenException f:
                    return new SendResult(SendOutcome.Rejected, "HTTP 403: " + f.Message);
                case UnauthorizedException _:
                    return new SendResult(SendOutcome.RetryLater, "HTTP 401");
                case RateLimitedException r:
                    return new SendResult(SendOutcome.RetryLater, "rate limited", r.RetryAfter);
                case ApiUnavailableException _:
                    return new SendResult(SendOutcome.RetryLater, "offline or server error");
                case OperationCanceledException _:
                    return new SendResult(SendOutcome.RetryLater, "timed out");
                default:
                    return new SendResult(SendOutcome.RetryLater, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Sends queued fixes in the background, oldest first. One failure that is worth retrying pauses the rest until
    /// the same time, so an offline machine makes one attempt per back-off step, not one per item.
    /// </summary>
    public sealed class SuggestionSubmitter
    {
        private static readonly Lazy<SuggestionSubmitter> SharedValue = new Lazy<SuggestionSubmitter>(() =>
            new SuggestionSubmitter(
                new SuggestionQueue(Path.Combine(TranslationLayerRegistry.DataRoot, "community-queue.json")),
                new ApiSuggestionSender(new KaptionApiClient(), () => App.LicenseService?.CurrentActivation?.DeviceSessionJwt),
                scheduleRetries: true));

        /// <summary>The app's submitter (queue file in <c>%APPDATA%\Kaption</c>).</summary>
        public static SuggestionSubmitter Shared => SharedValue.Value;

        private static readonly TimeSpan MinTimer = TimeSpan.FromSeconds(5);

        private readonly SuggestionQueue _queue;
        private readonly ISuggestionSender _sender;
        private readonly Func<DateTimeOffset> _clock;
        private readonly bool _scheduleRetries;
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private readonly object _timerGate = new object();
        private Timer _timer;
        private int _again;

        public SuggestionSubmitter(SuggestionQueue queue, ISuggestionSender sender, bool scheduleRetries, Func<DateTimeOffset> clock = null)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _scheduleRetries = scheduleRetries;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public SuggestionQueue Queue => _queue;

        /// <summary>Raised after a pass that changed the queue (any thread).</summary>
        public event EventHandler QueueChanged;

        /// <summary>Queues the fix (on disk first) and starts a send in the background. Never blocks on the network.</summary>
        public void Submit(PendingSuggestion suggestion)
        {
            _queue.Enqueue(suggestion);
            OnQueueChanged();
            Kick();
        }

        /// <summary>Takes back a waiting suggestion for a line whose fix was removed.</summary>
        public void Withdraw(string game, string lang, string enText)
        {
            if (_queue.Withdraw(game, lang, enText)) OnQueueChanged();
        }

        /// <summary>Starts a send pass unless one is running, in which case that pass runs once more.</summary>
        public void Kick()
        {
            _ = Task.Run(async () =>
            {
                try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Logger.Log.Warn($"Fix suggestions: send pass failed: {ex.Message}"); }
            });
        }

        /// <summary>Sends everything due; returns how many reached the server.</summary>
        public async Task<int> FlushAsync(CancellationToken ct)
        {
            int sent = 0;
            while (true)
            {
                // A pass is running: it runs once more for this request.
                if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _again, 1);
                    return sent;
                }
                try
                {
                    Interlocked.Exchange(ref _again, 0);
                    sent += await SendDueAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _flushGate.Release();
                }
                // Checked after the release, so a request that came in while finishing is never lost.
                if (Interlocked.Exchange(ref _again, 0) == 0) break;
            }
            ScheduleNext();
            return sent;
        }

        private async Task<int> SendDueAsync(CancellationToken ct)
        {
            int sent = 0;
            bool changed = false;
            foreach (var item in _queue.Due())
            {
                ct.ThrowIfCancellationRequested();
                SendResult result;
                try
                {
                    result = await _sender.SendAsync(item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = ApiSuggestionSender.Classify(ex);
                }

                changed = true;
                if (result.Outcome == SendOutcome.Sent)
                {
                    _queue.Complete(item.Id);
                    sent++;
                }
                else if (result.Outcome == SendOutcome.Rejected)
                {
                    _queue.Drop(item.Id, result.Error);
                }
                else
                {
                    var next = _queue.Retry(item.Id, result.Error, result.NotBefore);
                    _queue.DeferAll(next);
                    Logger.Log.Info($"Fix suggestions: will try again at {next.ToLocalTime():HH:mm} ({result.Error}).");
                    break;
                }
            }
            if (sent > 0) Logger.Log.Info($"Fix suggestions: {sent} sent for review.");
            if (changed) OnQueueChanged();
            return sent;
        }

        private void ScheduleNext()
        {
            if (!_scheduleRetries) return;
            var due = _queue.NextDue();
            if (due == null) return;
            var delay = due.Value - _clock();
            if (delay < MinTimer) delay = MinTimer;
            if (delay > SuggestionQueue.MaxRetry) delay = SuggestionQueue.MaxRetry;
            lock (_timerGate)
            {
                _timer ??= new Timer(_ => Kick());
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void OnQueueChanged()
        {
            try { QueueChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Logger.Log.Warn($"Fix suggestions: a queue listener threw: {ex.Message}"); }
        }
    }
}
