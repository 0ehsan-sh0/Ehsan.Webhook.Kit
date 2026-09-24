using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Deduplication;
using WebhookKit.Core.Diagnostics;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Processing;

public enum WebhookIngestionStatus
{
    Rejected = 0,
    Duplicate = 1,
    Processed = 2,
    Ignored = 3,
    Failed = 4,
    Accepted = 5,
    Admitted = Accepted
}

public sealed class WebhookIngestionRequest
{
    public required string WebhookId { get; init; }

    public required string Provider { get; init; }

    public required string HttpMethod { get; init; }

    public required string RequestPath { get; init; }

    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    public required ReadOnlyMemory<byte> RawBody { get; init; }

    public string? ContentType { get; init; }

    public long? ContentLength { get; init; }
}

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

    public WebhookIngestionStatus Status { get; }

    public WebhookRecord? Record { get; }

    public WebhookContext? Context { get; }

    public WebhookDispatchResult? DispatchResult { get; }

    public WebhookDispatchFailureKind FailureKind { get; }

    public string? FailureCode { get; }

    public string? FailureReason { get; }

    internal Exception? Exception { get; }

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
    private readonly ILogger<WebhookIngestionService> _logger;

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
        ILogger<WebhookIngestionService>? logger = null)
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
        _logger = logger ?? NullLogger<WebhookIngestionService>.Instance;
    }

    public Task<WebhookIngestionResult> AdmitAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Value.Storage.PersistRawBody)
        {
            throw new WebhookConfigurationException("Asynchronous webhook admission requires raw body persistence.");
        }

        return AdmitCoreAsync(request, cancellationToken);
    }

    public Task<WebhookIngestionResult> IngestForAsyncAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        return AdmitAsync(request, cancellationToken);
    }

    public async Task<WebhookIngestionResult> IngestAsync(
        WebhookIngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        var admission = await AdmitCoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (admission.Status != WebhookIngestionStatus.Accepted)
        {
            return admission;
        }

        var record = admission.Record!;
        var context = admission.Context!;
        WebhookDispatchResult dispatchResult;
        try
        {
            dispatchResult = await _processor
                .DispatchAsync(context, cancellationToken)
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
                record.Id,
                record.Provider,
                record.EventId,
                record.EventType,
                nameof(WebhookProcessingStatus.Failed),
                record.AttemptCount,
                traceId,
                "handler-failed");
            dispatchResult = WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Handler,
                "handler-failed",
                exception);
        }

        var ingestionStatus = dispatchResult.Status switch
        {
            WebhookDispatchStatus.Processed => WebhookIngestionStatus.Processed,
            WebhookDispatchStatus.Ignored => WebhookIngestionStatus.Ignored,
            _ => WebhookIngestionStatus.Failed
        };

        if (ingestionStatus == WebhookIngestionStatus.Processed)
        {
            record.Status = WebhookProcessingStatus.Processed;
            record.ProcessedAt = _clock.UtcNow;
            if (_options.Value.Storage.DiscardRawBodyAfterSuccessfulSync)
            {
                record.RawBody = null;
            }
        }
        else if (ingestionStatus == WebhookIngestionStatus.Ignored)
        {
            record.Status = WebhookProcessingStatus.Ignored;
            if (_options.Value.Storage.DiscardRawBodyAfterSuccessfulSync)
            {
                record.RawBody = null;
            }
        }
        else
        {
            record.Status = WebhookProcessingStatus.Failed;
            record.FailureReason = dispatchResult.FailureCode;
            record.FailureCode = dispatchResult.FailureCode;
            record.FailedAt = _clock.UtcNow;
        }

        try
        {
            await _store.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
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
                "status-update-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Handler,
                "status-update-failed",
                exception);
        }

        return WebhookIngestionResult.CreateDispatched(ingestionStatus, record, context, dispatchResult);
    }

    private async Task<WebhookIngestionResult> AdmitCoreAsync(
        WebhookIngestionRequest request,
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
            traceId);

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
            traceId);

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
                "event-id-extraction-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                "event-id-extraction-failed",
                exception);
        }

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
                "event-type-extraction-failed");
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                "event-type-extraction-failed",
                exception);
        }

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
                failureCode);
            return WebhookIngestionResult.CreateFailure(
                WebhookDispatchFailureKind.Payload,
                failureCode,
                exception);
        }

        var record = new WebhookRecord
        {
            Id = request.WebhookId,
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
                traceId);
            return WebhookIngestionResult.CreateDuplicate();
        }

        var context = new WebhookContext(rawBody, _deserializer)
        {
            WebhookId = record.Id,
            Provider = record.Provider,
            EventId = record.EventId,
            EventType = record.EventType,
            ReceivedAt = record.ReceivedAt,
            Headers = record.Headers
        };

        return WebhookIngestionResult.CreateAccepted(record, context);
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
