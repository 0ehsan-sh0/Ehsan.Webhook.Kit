using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Diagnostics;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Retries;

namespace WebhookKit.Core.Workers;

public sealed class WebhookBackgroundWorker : BackgroundService
{
    private const string HandlerFailureCode = "handler-failed";
    private const string PayloadFailureCode = "payload-invalid";
    private const string RawBodyMissingFailureCode = "raw-body-missing";
    private const string WorkerFailureCode = "worker-failed";
    private readonly IWebhookQueue _queue;
    private readonly IWebhookStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IWebhookClock _clock;
    private readonly ILogger<WebhookBackgroundWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IWebhookRetryExecutor _retryExecutor;
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _scheduledGate = new();
    private readonly Dictionary<string, ScheduledState> _scheduledItems = new(StringComparer.Ordinal);
    private int _maxScheduledItems = int.MaxValue;
    private int _externalQueueCompletionStarted;

    public WebhookBackgroundWorker(
        IWebhookQueue queue,
        IWebhookStore store,
        IServiceScopeFactory scopeFactory,
        IOptions<WebhookKitOptions> options,
        IWebhookClock clock,
        ILogger<WebhookBackgroundWorker>? logger = null,
        TimeProvider? timeProvider = null,
        IWebhookRetryExecutor? retryExecutor = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? NullLogger<WebhookBackgroundWorker>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryExecutor = retryExecutor ?? new WebhookRetryExecutor(new WebhookRetryPolicy(), new TaskWebhookRetryDelay());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _options.Value;
        if (!settings.Background.Enabled)
        {
            return;
        }

        var concurrency = Math.Max(1, settings.Background.WorkerConcurrency);
        var capacity = Math.Max(1, settings.Queue.Capacity);
        _maxScheduledItems = capacity > int.MaxValue - concurrency ? int.MaxValue : capacity + concurrency;
        var workChannel = Channel.CreateBounded<WebhookWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = concurrency == 1,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        using var queueCompleted = new CancellationTokenSource();
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, queueCompleted.Token);
        var readerTask = ReadExternalQueueAsync(workChannel.Writer, queueCompleted, stoppingToken);
        var recoveryTask = RunRecoveryLoopAsync(recoveryCancellation.Token);
        var processorTasks = Enumerable.Range(0, concurrency)
            .Select(_ => ProcessInternalQueueAsync(workChannel.Reader, stoppingToken))
            .ToArray();

        try
        {
            await readerTask.ConfigureAwait(false);
            queueCompleted.Cancel();
            await recoveryTask.ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.WhenAll(processorTasks).WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            workChannel.Writer.TryComplete();
            await CompleteExternalQueueOnceAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadExternalQueueAsync(
        ChannelWriter<WebhookWorkItem> writer,
        CancellationTokenSource queueCompleted,
        CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                WebhookWorkItem workItem;
                try
                {
                    workItem = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    queueCompleted.Cancel();
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    queueCompleted.Cancel();
                    return;
                }

                if (workItem is null)
                {
                    queueCompleted.Cancel();
                    return;
                }

                if (!TryMarkActive(workItem.WebhookId))
                {
                    continue;
                }

                try
                {
                    await writer.WriteAsync(workItem, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    RemoveScheduled(workItem.WebhookId);
                    return;
                }
                catch
                {
                    RemoveScheduled(workItem.WebhookId);
                    queueCompleted.Cancel();
                    return;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task RunRecoveryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(cancellationToken).ConfigureAwait(false);
            var interval = _options.Value.Background.RecoveryInterval;
            if (interval <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromMilliseconds(1);
            }

            using var timer = new PeriodicTimer(interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RecoverAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<WebhookRecord> records;
            try
            {
                var background = _options.Value.Background;
                records = await _store.GetRecoverableAsync(
                    _clock.UtcNow,
                    background.RecoveryAge,
                    Math.Max(1, background.RecoveryBatchSize),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return;
            }

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record is null || IsTerminal(record.Status) || !TryScheduleQueued(record.Id))
                {
                    continue;
                }

                var accepted = false;
                try
                {
                    accepted = await _queue.TryEnqueueAsync(
                        new WebhookWorkItem(record.Id, record.Provider),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RemoveScheduled(record.Id);
                    throw;
                }
                catch
                {
                    RemoveScheduled(record.Id);
                    continue;
                }

                if (!accepted)
                {
                    RemoveScheduled(record.Id);
                }
            }
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task ProcessInternalQueueAsync(
        ChannelReader<WebhookWorkItem> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var workItem in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessWorkItemAsync(workItem, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task ProcessWorkItemAsync(WebhookWorkItem workItem, CancellationToken cancellationToken)
    {
        WebhookRecord? record = null;
        var claimed = false;
        var correlationId = string.Empty;
        string? traceId = null;
        var attempt = 0;

        try
        {
            record = await _store.GetByWebhookIdAsync(workItem.WebhookId, cancellationToken).ConfigureAwait(false);
            if (record is null || IsTerminal(record.Status) || !ProviderMatches(record, workItem))
            {
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            claimed = await _store.TryClaimAsync(
                workItem.WebhookId,
                _leaseOwner,
                _options.Value.Background.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                return;
            }

            var claimedRecord = await _store.GetByWebhookIdAsync(workItem.WebhookId, cancellationToken).ConfigureAwait(false);
            if (claimedRecord is null)
            {
                await ReleaseAfterCancellationAsync(workItem.WebhookId).ConfigureAwait(false);
                claimed = false;
                return;
            }

            record = claimedRecord;
            attempt = record.AttemptCount;
            record.Status = WebhookProcessingStatus.Processing;
            record.ProcessingLeaseOwner = _leaseOwner;
            correlationId = GetCorrelationId(record);
            traceId = Activity.Current?.TraceId.ToString();
            WebhookLogMessages.Processing(
                _logger,
                record.Id,
                record.Provider,
                record.EventId,
                record.EventType,
                nameof(WebhookProcessingStatus.Processing),
                attempt,
                traceId,
                correlationId);

            if (record.RawBody is null)
            {
                await MarkFailedAsync(record, RawBodyMissingFailureCode, traceId, correlationId, attempt, cancellationToken).ConfigureAwait(false);
                return;
            }

            var deserializer = scope.ServiceProvider.GetRequiredService<IWebhookDeserializer>();
            var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
            var context = new WebhookContext(record.RawBody, deserializer)
            {
                WebhookId = record.Id,
                CorrelationId = correlationId,
                Provider = record.Provider,
                EventId = record.EventId,
                EventType = record.EventType,
                ReceivedAt = record.ReceivedAt,
                ProviderTimestamp = record.ProviderTimestamp,
                Headers = record.Headers
            };

            var retryOptions = GetRetryOptions(record);
            var result = await _retryExecutor.ExecuteAsync(
                processor,
                context,
                retryOptions,
                (currentAttempt, token) => PersistAttemptAsync(record, currentAttempt, token),
                (currentAttempt, nextDelay, failureCode, _) =>
                {
                    WebhookLogMessages.Retry(
                        _logger,
                        record.Id,
                        record.Provider,
                        record.EventId,
                        record.EventType,
                        nameof(WebhookProcessingStatus.Processing),
                        currentAttempt,
                        nextDelay,
                        traceId,
                        correlationId,
                        failureCode);
                    return Task.CompletedTask;
                },
                firstAttempt: record.AttemptCount,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            attempt = record.AttemptCount;

            switch (result.Status)
            {
                case WebhookDispatchStatus.Processed:
                    await MarkProcessedAsync(record, traceId, correlationId, attempt, cancellationToken).ConfigureAwait(false);
                    break;
                case WebhookDispatchStatus.Ignored:
                    await MarkIgnoredAsync(record, traceId, correlationId, attempt, cancellationToken).ConfigureAwait(false);
                    break;
                case WebhookDispatchStatus.Failed:
                    await MarkFailedAsync(
                        record,
                        ResolveFailureCode(result),
                        traceId,
                        correlationId,
                        attempt,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await MarkFailedAsync(record, WorkerFailureCode, traceId, correlationId, attempt, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (claimed)
            {
                await ReleaseAfterCancellationAsync(workItem.WebhookId).ConfigureAwait(false);
            }

            throw;
        }
        catch
        {
            if (claimed && record is not null)
            {
                await MarkFailedAsync(
                    record,
                    WorkerFailureCode,
                    traceId,
                    correlationId,
                    attempt,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            RemoveScheduled(workItem.WebhookId);
        }
    }

    private WebhookRetryOptions GetRetryOptions(WebhookRecord record)
    {
        return _options.Value.Providers.TryGetValue(record.Provider, out var provider)
            ? provider.Retry
            : new WebhookRetryOptions();
    }

    private async Task PersistAttemptAsync(
        WebhookRecord record,
        int attempt,
        CancellationToken cancellationToken)
    {
        record.AttemptCount = attempt;
        record.LastAttemptAt = _clock.UtcNow;
        record.Status = WebhookProcessingStatus.Processing;
        record.ProcessingLeaseOwner = _leaseOwner;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkProcessedAsync(
        WebhookRecord record,
        string? traceId,
        string correlationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            var marked = await _store.MarkProcessedAsync(
                record.Id,
                _leaseOwner,
                _clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            if (!marked)
            {
                return;
            }

            WebhookLogMessages.Processed(
                _logger,
                record.Id,
                record.Provider,
                record.EventId,
                record.EventType,
                nameof(WebhookProcessingStatus.Processed),
                attempt,
                traceId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            LogStatusUpdateFailure(record, traceId, correlationId, attempt);
        }
    }

    private async Task MarkIgnoredAsync(
        WebhookRecord record,
        string? traceId,
        string correlationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            record.Status = WebhookProcessingStatus.Ignored;
            record.ProcessedAt = null;
            record.FailedAt = null;
            record.FailureReason = null;
            record.FailureCode = null;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

            record.ProcessingLeaseOwner = null;
            record.ProcessingLeaseExpiresAt = null;
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            WebhookLogMessages.Ignored(
                _logger,
                record.Id,
                record.Provider,
                record.EventId,
                record.EventType,
                nameof(WebhookProcessingStatus.Ignored),
                attempt,
                traceId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            LogStatusUpdateFailure(record, traceId, correlationId, attempt);
        }
    }

    private async Task MarkFailedAsync(
        WebhookRecord record,
        string failureCode,
        string? traceId,
        string correlationId,
        int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            var marked = await _store.MarkFailedAsync(
                record.Id,
                _leaseOwner,
                _clock.UtcNow,
                failureCode,
                cancellationToken).ConfigureAwait(false);
            if (!marked)
            {
                return;
            }

            WebhookLogMessages.Failed(
                _logger,
                record.Id,
                record.Provider,
                record.EventId,
                record.EventType,
                nameof(WebhookProcessingStatus.Failed),
                attempt,
                traceId,
                correlationId,
                failureCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            LogStatusUpdateFailure(record, traceId, correlationId, attempt);
        }
    }

    private async Task ReleaseAfterCancellationAsync(string webhookId)
    {
        try
        {
            await _store.ReleaseAsync(webhookId, _leaseOwner, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void LogStatusUpdateFailure(WebhookRecord record, string? traceId, string correlationId, int attempt)
    {
        WebhookLogMessages.Failed(
            _logger,
            record.Id,
            record.Provider,
            record.EventId,
            record.EventType,
            nameof(WebhookProcessingStatus.Failed),
            attempt,
            traceId,
            correlationId,
            "status-update-failed");
    }

    private static string ResolveFailureCode(WebhookDispatchResult result)
    {
        var fallback = result.FailureKind == WebhookDispatchFailureKind.Payload
            ? PayloadFailureCode
            : HandlerFailureCode;
        return IsSafeCode(result.FailureCode) ? result.FailureCode! : fallback;
    }

    private static bool IsSafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTerminal(WebhookProcessingStatus status)
    {
        return status is WebhookProcessingStatus.Processed or
            WebhookProcessingStatus.Failed or
            WebhookProcessingStatus.Ignored or
            WebhookProcessingStatus.Duplicate;
    }

    private static bool ProviderMatches(WebhookRecord record, WebhookWorkItem workItem)
    {
        return string.Equals(record.Provider, workItem.Provider, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCorrelationId(WebhookRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.CorrelationId) &&
            !string.Equals(record.CorrelationId, record.Id, StringComparison.Ordinal) &&
            !string.Equals(record.CorrelationId, record.EventId, StringComparison.Ordinal))
        {
            return record.CorrelationId;
        }

        return WebhookDiagnostics.CreateCorrelationId();
    }

    private bool TryScheduleQueued(string webhookId)
    {
        lock (_scheduledGate)
        {
            if (_scheduledItems.ContainsKey(webhookId) || _scheduledItems.Count >= _maxScheduledItems)
            {
                return false;
            }

            _scheduledItems.Add(webhookId, ScheduledState.Queued);
            return true;
        }
    }

    private bool TryMarkActive(string webhookId)
    {
        lock (_scheduledGate)
        {
            if (!_scheduledItems.TryGetValue(webhookId, out var state))
            {
                if (_scheduledItems.Count >= _maxScheduledItems)
                {
                    return false;
                }

                _scheduledItems.Add(webhookId, ScheduledState.Active);
                return true;
            }

            if (state == ScheduledState.Active)
            {
                return false;
            }

            _scheduledItems[webhookId] = ScheduledState.Active;
            return true;
        }
    }

    private void RemoveScheduled(string webhookId)
    {
        lock (_scheduledGate)
        {
            _scheduledItems.Remove(webhookId);
        }
    }

    private async Task CompleteExternalQueueOnceAsync()
    {
        if (Interlocked.Exchange(ref _externalQueueCompletionStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await _queue.CompleteAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private enum ScheduledState
    {
        Queued,
        Active
    }
}
