using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Deduplication;
using WebhookKit.Core.Diagnostics;
using WebhookKit.Core.Options;
using WebhookKit.Core.Retries;

namespace WebhookKit.Core.Processing;

/// <summary>Outcome of verification, admission, and processing for one delivery.</summary>
public enum WebhookIngestionStatus
{
    /// <summary>Verification or required metadata rejected the delivery.</summary>
    Rejected = 0,
    /// <summary>The provider event was already recorded.</summary>
    Duplicate = 1,
    /// <summary>Synchronous processing completed successfully.</summary>
    Processed = 2,
    /// <summary>No handler matched the verified event type.</summary>
    Ignored = 3,
    /// <summary>Processing failed after the configured retry policy.</summary>
    Failed = 4,
    /// <summary>The delivery was admitted for asynchronous processing.</summary>
    Accepted = 5,
    /// <summary>Compatibility alias for <see cref="Accepted"/>.</summary>
    Admitted = Accepted
}

/// <summary>Request metadata and exact bytes supplied to the ingestion service.</summary>
public sealed class WebhookIngestionRequest
{
    /// <summary>WebhookKit transmission identifier assigned at ingress.</summary>
    public required string WebhookId { get; init; }

    /// <summary>Optional application correlation identifier.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Configured provider name.</summary>
    public required string Provider { get; init; }

    /// <summary>HTTP method of the ingress request.</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Request path of the ingress request.</summary>
    public required string RequestPath { get; init; }

    /// <summary>Request headers; values are copied before asynchronous processing.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    /// <summary>Exact request bytes used for verification and payload deserialization.</summary>
    public required ReadOnlyMemory<byte> RawBody { get; init; }

    /// <summary>Optional request content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional request content length.</summary>
    public long? ContentLength { get; init; }
}

/// <summary>Safe result of synchronous or asynchronous webhook ingestion.</summary>
public sealed class WebhookIngestionResult
{
    private WebhookIngestionResult(
        WebhookIngestionStatus status,
        WebhookRecord? record,
        WebhookContext? context,
        WebhookDispatchResult? dispatchResult,
        WebhookDispatchFailureKind failureKind,
        string? failureCode,
        string? failureReason,
        Exception? exception)
    {
        Status = status;
        Record = record;
        Context = context;
        DispatchResult = dispatchResult;
        FailureKind = failureKind;
        FailureCode = failureCode;
        FailureReason = failureReason;
        Exception = exception;
    }

    /// <summary>Overall ingestion outcome.</summary>
    public WebhookIngestionStatus Status { get; }

    /// <summary>Persisted record when one was admitted, otherwise <see langword="null"/>.</summary>
    public WebhookRecord? Record { get; }

    /// <summary>Verified context when payload processing was admitted, otherwise <see langword="null"/>.</summary>
    public WebhookContext? Context { get; }

    /// <summary>Dispatch result when processing ran, otherwise <see langword="null"/>.</summary>
    public WebhookDispatchResult? DispatchResult { get; }

    /// <summary>Safe classification of a failure.</summary>
    public WebhookDispatchFailureKind FailureKind { get; }

    /// <summary>Stable safe failure code, when applicable.</summary>
    public string? FailureCode { get; }

    /// <summary>Safe failure reason, when applicable; never contains secrets or raw payload data.</summary>
    public string? FailureReason { get; }

    internal Exception? Exception { get; }

    /// <summary>Returns a compact status and safe failure code.</summary>
    /// <returns>A diagnostic string without exception details.</returns>
    public override string ToString()
    {
        return FailureCode is null
            ? Status.ToString()
            : $"{Status}:{FailureCode}";
    }

    internal static WebhookIngestionResult CreateRejected(string failureCode, string? failureReason)
    {
        return new WebhookIngestionResult(
            WebhookIngestionStatus.Rejected,
            null,
            null,
            null,
            WebhookDispatchFailureKind.None,
            failureCode,
            failureReason,
            null);
    }

    internal static WebhookIngestionResult CreateDuplicate()
    {
        return new WebhookIngestionResult(
            WebhookIngestionStatus.Duplicate,
            null,
            null,
            null,
            WebhookDispatchFailureKind.None,
            null,
            null,
            null);
    }

    internal static WebhookIngestionResult CreateFailure(
        WebhookDispatchFailureKind failureKind,
        string failureCode,
        Exception? exception = null,
        string? failureReason = null)
    {
        return new WebhookIngestionResult(
            WebhookIngestionStatus.Failed,
            null,
            null,
            null,
            failureKind,
            failureCode,
            failureReason,
            exception);
    }

    internal static WebhookIngestionResult CreateAccepted(
        WebhookRecord record,
        WebhookContext context)
    {
        return new WebhookIngestionResult(
            WebhookIngestionStatus.Accepted,
            record,
            context,
            null,
            WebhookDispatchFailureKind.None,
            null,
            null,
            null);
    }

    internal static WebhookIngestionResult CreateDispatched(
        WebhookIngestionStatus status,
        WebhookRecord record,
        WebhookContext context,
        WebhookDispatchResult dispatchResult)
    {
        return new WebhookIngestionResult(
            status,
            record,
            context,
            dispatchResult,
            dispatchResult.FailureKind,
            dispatchResult.FailureCode,
            null,
            dispatchResult.FailureException);
    }
}

/// <summary>Coordinates verification, metadata extraction, deduplication, and dispatch.</summary>
public sealed class WebhookIngestionService
{
    private readonly IWebhookSignatureVerifier _signatureVerifier;
    private readonly IWebhookTimestampVerifier _timestampVerifier;
    private readonly IWebhookEventIdExtractor _eventIdExtractor;
    private readonly IWebhookEventTypeExtractor _eventTypeExtractor;
    private readonly IWebhookDeduplicator _deduplicator;
    private readonly WebhookDeduplicationKeyFactory _keyFactory;
    private readonly IWebhookDeserializer _deserializer;
    private readonly IWebhookStore _store;
    private readonly IWebhookClock _clock;
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IWebhookDispatchProcessor _processor;
    private readonly IWebhookRetryExecutor _retryExecutor;
    private readonly ILogger<WebhookIngestionService> _logger;

    /// <summary>Creates the ingestion pipeline with its verification, storage, and dispatch dependencies.</summary>
    /// <param name="signatureVerifier">Authenticates the exact request bytes.</param>
    /// <param name="timestampVerifier">Validates replay freshness.</param>
    /// <param name="eventIdExtractor">Extracts the provider event identifier.</param>
    /// <param name="eventTypeExtractor">Extracts the provider event type.</param>
    /// <param name="deduplicator">Atomically claims the provider-scoped delivery.</param>
    /// <param name="keyFactory">Creates the deduplication key.</param>
    /// <param name="deserializer">Deserializes admitted payloads.</param>
    /// <param name="store">Persists records and processing leases.</param>
    /// <param name="clock">Supplies deterministic timestamps.</param>
    /// <param name="options">Current WebhookKit options.</param>
    /// <param name="processor">Dispatches verified contexts.</param>
    /// <param name="logger">Optional logger; a null logger uses a no-op logger.</param>
    /// <param name="retryExecutor">Optional retry executor; a default executor is created when omitted.</param>
    public WebhookIngestionService(
        IWebhookSignatureVerifier signatureVerifier,
        IWebhookTimestampVerifier timestampVerifier,
        IWebhookEventIdExtractor eventIdExtractor,
        IWebhookEventTypeExtractor eventTypeExtractor,
        IWebhookDeduplicator deduplicator,
        WebhookDeduplicationKeyFactory keyFactory,
        IWebhookDeserializer deserializer,
        IWebhookStore store,
        IWebhookClock clock,
        IOptions<WebhookKitOptions> options,
        IWebhookDispatchProcessor processor,
        ILogger<WebhookIngestionService>? logger = null,
        IWebhookRetryExecutor? retryExecutor = null)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _timestampVerifier = timestampVerifier ?? throw new ArgumentNullException(nameof(timestampVerifier));
        _eventIdExtractor = eventIdExtractor ?? throw new ArgumentNullException(nameof(eventIdExtractor));
        _eventTypeExtractor = eventTypeExtractor ?? throw new ArgumentNullException(nameof(eventTypeExtractor));
        _deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        _keyFactory = keyFactory ?? throw new ArgumentNullException(nameof(keyFactory));
        _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _retryExecutor = retryExecutor ?? new WebhookRetryExecutor(new WebhookRetryPolicy(), new TaskWebhookRetryDelay());
        _logger = logger ?? NullLogger<WebhookIngestionService>.Instance;
    }

    /// <summary>Verifies, extracts, deduplicates, and admits a delivery for asynchronous processing.</summary>
    /// <param name="request">Ingress metadata and exact request bytes.</param>
    /// <param name="cancellationToken">Token used to cancel admission.</param>
    /// <returns>An accepted, duplicate, rejected, or failed result.</returns>
    /// <exception cref="WebhookConfigurationException">Asynchronous admission is configured without raw-body persistence.</exception>
    public Task<WebhookIngestionResult> AdmitAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Value.Storage.PersistRawBody)
        {
            throw new WebhookConfigurationException("Asynchronous webhook admission requires raw body persistence.");
        }

        return AdmitWithActivityAsync(request, cancellationToken);
    }

    /// <summary>Compatibility alias for <see cref="AdmitAsync"/>.</summary>
    /// <param name="request">Ingress metadata and exact request bytes.</param>
    /// <param name="cancellationToken">Token used to cancel admission.</param>
    /// <returns>The asynchronous admission result.</returns>
    public Task<WebhookIngestionResult> IngestForAsyncAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        return AdmitAsync(request, cancellationToken);
    }

    /// <summary>Runs synchronous verification, deduplication, and handler dispatch.</summary>
    /// <param name="request">Ingress metadata and exact request bytes.</param>
    /// <param name="cancellationToken">Token used to cancel verification, storage, retries, and handlers.</param>
    /// <returns>A processed, ignored, duplicate, rejected, or failed result.</returns>
    public async Task<WebhookIngestionResult> IngestAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = ResolveCorrelationId(request.WebhookId, request.CorrelationId, Activity.Current?.TraceId.ToString());
        using var activity = WebhookDiagnostics.StartReceive(request.WebhookId, request.Provider, correlationId);
        var leaseOwner = Guid.NewGuid().ToString("N");
        var claimed = false;
        WebhookRecord? record = null;

        try
        {
            var traceId = Activity.Current?.TraceId.ToString();
            var admission = await AdmitCoreAsync(request, correlationId, activity, cancellationToken).ConfigureAwait(false);
            if (admission.Status != WebhookIngestionStatus.Accepted)
            {
                SetReceiveResult(activity, admission.Status);
                return admission;
            }

            record = admission.Record!;
            correlationId = record.CorrelationId ?? correlationId;
            var context = admission.Context!;
            claimed = await _store.TryClaimAsync(
                record.Id,
                leaseOwner,
                _options.Value.Background.LeaseDuration,
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                WebhookLogMessages.Failed(
                    _logger,
                    record.Id,
                    record.Provider,
                    record.EventId,
                    record.EventType,
                    nameof(WebhookProcessingStatus.Failed),
                    record.AttemptCount,
                    traceId,
                    correlationId,
                    "processing-claim-failed");
                SetReceiveResult(activity, WebhookIngestionStatus.Failed);
                return WebhookIngestionResult.CreateFailure(
                    WebhookDispatchFailureKind.Handler,
                    "processing-claim-failed");
            }

            var claimedRecord = await _store.GetByWebhookIdAsync(record.Id, cancellationToken).ConfigureAwait(false);
            if (claimedRecord is null)
            {
                await ReleaseClaimAsync(record.Id, leaseOwner).ConfigureAwait(false);
                claimed = false;
                SetReceiveResult(activity, WebhookIngestionStatus.Failed);
                return WebhookIngestionResult.CreateFailure(
                    WebhookDispatchFailureKind.Handler,
                    "processing-claim-failed");
            }

            record = claimedRecord;
            record.Status = WebhookProcessingStatus.Processing;
            record.ProcessingLeaseOwner = leaseOwner;
            var retryOptions = GetRetryOptions(record);

            var dispatchResult = await _retryExecutor.ExecuteAsync(
                _processor,
                context,
                retryOptions,
                (attempt, token) => PersistAttemptAsync(record, leaseOwner, attempt, token),
                (attempt, nextDelay, failureCode, token) =>
                {
                    WebhookLogMessages.Retry(
                        _logger,
                        record.Id,
                        record.Provider,
                        record.EventId,
                        record.EventType,
                        nameof(WebhookProcessingStatus.Processing),
                        attempt,
                        nextDelay,
                        traceId,
                        correlationId,
                        failureCode);
                    return Task.CompletedTask;
                },
                firstAttempt: record.AttemptCount,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var ingestionStatus = dispatchResult.Status switch
            {
                WebhookDispatchStatus.Processed => WebhookIngestionStatus.Processed,
                WebhookDispatchStatus.Ignored => WebhookIngestionStatus.Ignored,
                _ => WebhookIngestionStatus.Failed
            };

            try
            {
                if (ingestionStatus == WebhookIngestionStatus.Processed)
                {
                    var processedAt = _clock.UtcNow;
                    await _store.MarkProcessedAsync(record.Id, leaseOwner, processedAt, cancellationToken).ConfigureAwait(false);
                    record.Status = WebhookProcessingStatus.Processed;
                    record.ProcessedAt = processedAt;
                    record.ProcessingLeaseOwner = null;
                    record.ProcessingLeaseExpiresAt = null;
                    if (_options.Value.Storage.DiscardRawBodyAfterSuccessfulSync)
                    {
                        record.RawBody = null;
                        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (ingestionStatus == WebhookIngestionStatus.Ignored)
                {
                    if (_options.Value.Storage.DiscardRawBodyAfterSuccessfulSync)
                    {
                        record.RawBody = null;
                    }

                    await MarkIgnoredAsync(record, leaseOwner, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var failedAt = _clock.UtcNow;
                    var failureCode = dispatchResult.FailureCode ?? "handler-failed";
                    await _store.MarkFailedAsync(record.Id, leaseOwner, failedAt, failureCode, cancellationToken).ConfigureAwait(false);
                    record.Status = WebhookProcessingStatus.Failed;
                    record.FailureReason = failureCode;
                    record.FailureCode = failureCode;
                    record.FailedAt = failedAt;
                    record.ProcessingLeaseOwner = null;
                    record.ProcessingLeaseExpiresAt = null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                WebhookLogMessages.Failed(
                    _logger,
                    record.Id,
                    record.Provider,
                    record.EventId,
                    record.EventType,
                    nameof(WebhookProcessingStatus.Failed),
                    record.AttemptCount,
                    traceId,
                    correlationId,
                    "status-update-failed");
                SetReceiveResult(activity, WebhookIngestionStatus.Failed);
                return WebhookIngestionResult.CreateFailure(
                    WebhookDispatchFailureKind.Handler,
                    "status-update-failed",
                    exception);
            }

            SetReceiveResult(activity, ingestionStatus);
            return WebhookIngestionResult.CreateDispatched(ingestionStatus, record, context, dispatchResult);
        }
        catch (OperationCanceledException)
        {
            if (claimed && record is not null)
            {
                await ReleaseClaimAsync(record.Id, leaseOwner).ConfigureAwait(false);
            }

            WebhookDiagnostics.SetResult(activity, "Cancelled", true);
            throw;
        }
        catch (Exception)
        {
            WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Failed), true);
            throw;
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
        string leaseOwner,
        int attempt,
        CancellationToken cancellationToken)
    {
        record.AttemptCount = attempt;
        record.LastAttemptAt = _clock.UtcNow;
        record.Status = WebhookProcessingStatus.Processing;
        record.ProcessingLeaseOwner = leaseOwner;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkIgnoredAsync(
        WebhookRecord record,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        record.Status = WebhookProcessingStatus.Ignored;
        record.ProcessedAt = null;
        record.FailedAt = null;
        record.FailureReason = null;
        record.FailureCode = null;
        record.ProcessingLeaseOwner = leaseOwner;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

        record.ProcessingLeaseOwner = null;
        record.ProcessingLeaseExpiresAt = null;
        await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseClaimAsync(string webhookId, string leaseOwner)
    {
        try
        {
            await _store.ReleaseAsync(webhookId, leaseOwner, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<WebhookIngestionResult> AdmitWithActivityAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = ResolveCorrelationId(request.WebhookId, request.CorrelationId, Activity.Current?.TraceId.ToString());
        using var activity = WebhookDiagnostics.StartReceive(request.WebhookId, request.Provider, correlationId);

        try
        {
            var result = await AdmitCoreAsync(request, correlationId, activity, cancellationToken).ConfigureAwait(false);
            SetReceiveResult(activity, result.Status);
            return result;
        }
        catch (OperationCanceledException)
        {
            WebhookDiagnostics.SetResult(activity, "Cancelled", true);
            throw;
        }
        catch (Exception)
        {
            WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Failed), true);
            throw;
        }
    }

    private async Task<WebhookIngestionResult> AdmitCoreAsync(
        WebhookIngestionRequest request,
        string correlationId,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var traceId = Activity.Current?.TraceId.ToString();

        WebhookLogMessages.Received(
            _logger,
            request.WebhookId,
            request.Provider,
            null,
            null,
            nameof(WebhookProcessingStatus.Received),
            0,
            traceId,
            correlationId);

        var rawBody = request.RawBody.ToArray();
        var headers = CopyHeaders(request.Headers);
        var verificationContext = new WebhookVerificationContext
        {
            Provider = request.Provider,
            RawBody = rawBody,
            Headers = headers
        };

        WebhookVerificationResult signatureResult;
        try
        {
            signatureResult = await _signatureVerifier
                .VerifyAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                null,
                null,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "signature-verification-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Handler,
                "signature-verification-failed",
                exception);
        }

        if (!signatureResult.IsValid)
        {
            WebhookLogMessages.Rejected(
                _logger,
                request.WebhookId,
                request.Provider,
                null,
                null,
                "Rejected",
                0,
                traceId,
                correlationId,
                "signature-verification-failed");
            return WebhookIngestionResult.CreateRejected(
                "signature-verification-failed",
                signatureResult.FailureReason);
        }

        WebhookVerificationResult timestampResult;
        try
        {
            timestampResult = await _timestampVerifier
                .VerifyAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                null,
                null,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "timestamp-verification-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Handler,
                "timestamp-verification-failed",
                exception);
        }

        if (!timestampResult.IsValid)
        {
            WebhookLogMessages.Rejected(
                _logger,
                request.WebhookId,
                request.Provider,
                null,
                null,
                "Rejected",
                0,
                traceId,
                correlationId,
                "timestamp-verification-failed");
            return WebhookIngestionResult.CreateRejected(
                "timestamp-verification-failed",
                timestampResult.FailureReason);
        }

        WebhookLogMessages.Verified(
            _logger,
            request.WebhookId,
            request.Provider,
            null,
            null,
            "Verified",
            0,
            traceId,
            correlationId);

        string? eventId;
        try
        {
            var extractedEventId = await _eventIdExtractor
                .ExtractAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false);
            eventId = string.IsNullOrWhiteSpace(extractedEventId) ? null : extractedEventId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                null,
                null,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "event-id-extraction-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                "event-id-extraction-failed",
                exception);
        }

        correlationId = EnsureDistinctCorrelationId(correlationId, request.WebhookId, eventId);
        WebhookDiagnostics.SetTags(
            activity,
            request.WebhookId,
            request.Provider,
            eventId,
            null,
            "Verified",
            correlationId);

        string? eventType;
        try
        {
            var extractedEventType = await _eventTypeExtractor
                .ExtractAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false);
            eventType = string.IsNullOrWhiteSpace(extractedEventType) ? null : extractedEventType;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                null,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "event-type-extraction-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                "event-type-extraction-failed",
                exception);
        }

        WebhookDiagnostics.SetTags(
            activity,
            request.WebhookId,
            request.Provider,
            eventId,
            eventType,
            "Verified",
            correlationId);

        if (eventType is null)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                null,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "missing-event-type");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                "missing-event-type");
        }

        if (!_options.Value.Providers.TryGetValue(request.Provider, out var providerOptions))
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                eventType,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "provider-not-configured");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Handler,
                "provider-not-configured");
        }

        string deduplicationKey;
        try
        {
            deduplicationKey = _keyFactory.Create(request.Provider, eventId, rawBody, providerOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = eventId is null ? "event-id-required" : "deduplication-failed";
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                eventType,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                failureCode);
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                failureCode,
                exception);
        }

        var record = new WebhookRecord
        {
            Id = request.WebhookId,
            CorrelationId = correlationId,
            Provider = request.Provider,
            EventId = eventId,
            EventType = eventType,
            DeduplicationKey = deduplicationKey,
            HttpMethod = request.HttpMethod,
            RequestPath = request.RequestPath,
            Headers = headers,
            ContentType = request.ContentType,
            ContentLength = request.ContentLength,
            RawBody = _options.Value.Storage.PersistRawBody ? rawBody : null,
            ReceivedAt = _clock.UtcNow,
            Status = WebhookProcessingStatus.Received
        };

        bool acquired;
        try
        {
            acquired = await _deduplicator
                .TryAcquireAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            WebhookLogMessages.Failed(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                eventType,
                nameof(WebhookProcessingStatus.Failed),
                0,
                traceId,
                correlationId,
                "deduplication-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Handler,
                "deduplication-failed",
                exception);
        }

        if (!acquired)
        {
            WebhookLogMessages.Duplicate(
                _logger,
                request.WebhookId,
                request.Provider,
                eventId,
                eventType,
                nameof(WebhookProcessingStatus.Duplicate),
                0,
                traceId,
                correlationId);
            return WebhookIngestionResult.CreateDuplicate();
        }

        var context = new WebhookContext(rawBody, _deserializer)
        {
            WebhookId = record.Id,
            CorrelationId = record.CorrelationId,
            Provider = record.Provider,
            EventId = record.EventId,
            EventType = record.EventType,
            ReceivedAt = record.ReceivedAt,
            Headers = record.Headers
        };

        return WebhookIngestionResult.CreateAccepted(record, context);
    }

    private static void SetReceiveResult(Activity? activity, WebhookIngestionStatus status)
    {
        var statusText = status switch
        {
            WebhookIngestionStatus.Processed => nameof(WebhookProcessingStatus.Processed),
            WebhookIngestionStatus.Ignored => nameof(WebhookProcessingStatus.Ignored),
            WebhookIngestionStatus.Duplicate => nameof(WebhookProcessingStatus.Duplicate),
            WebhookIngestionStatus.Rejected => nameof(WebhookIngestionStatus.Rejected),
            WebhookIngestionStatus.Accepted => nameof(WebhookIngestionStatus.Accepted),
            _ => nameof(WebhookProcessingStatus.Failed)
        };
        WebhookDiagnostics.SetResult(
            activity,
            statusText,
            status is WebhookIngestionStatus.Rejected or WebhookIngestionStatus.Failed);
    }

    private static string ResolveCorrelationId(
        string webhookId,
        string? suppliedCorrelationId,
        string? ambientCorrelationId)
    {
        var correlationId = string.IsNullOrWhiteSpace(suppliedCorrelationId)
            ? ambientCorrelationId
            : suppliedCorrelationId;
        correlationId = string.IsNullOrWhiteSpace(correlationId)
            ? null
            : correlationId.Trim();
        return EnsureDistinctCorrelationId(correlationId, webhookId, null);
    }

    private static string EnsureDistinctCorrelationId(
        string? correlationId,
        string webhookId,
        string? eventId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId) &&
            !string.Equals(correlationId, webhookId, StringComparison.Ordinal) &&
            !string.Equals(correlationId, eventId, StringComparison.Ordinal))
        {
            return correlationId;
        }

        string generated;
        do
        {
            generated = WebhookDiagnostics.CreateCorrelationId();
        }
        while (string.Equals(generated, webhookId, StringComparison.Ordinal) ||
               string.Equals(generated, eventId, StringComparison.Ordinal));

        return generated;
    }

    private static void ValidateRequest(WebhookIngestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WebhookId))
        {
            throw new ArgumentException("Webhook ID must be non-empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            throw new ArgumentException("Provider must be non-empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.HttpMethod))
        {
            throw new ArgumentException("HTTP method must be non-empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RequestPath))
        {
            throw new ArgumentException("Request path must be non-empty.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Headers);
        if (request.ContentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Content length cannot be negative.");
        }
    }

    private static ReadOnlyDictionary<string, string[]> CopyHeaders(
        IReadOnlyDictionary<string, string[]> headers)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            ArgumentNullException.ThrowIfNull(header.Key);
            ArgumentNullException.ThrowIfNull(header.Value);
            snapshot[header.Key] = (string[])header.Value.Clone();
        }

        return new ReadOnlyDictionary<string, string[]>(snapshot);
    }
}
