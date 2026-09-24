using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Diagnostics;
using WebhookKit.AspNetCore.Exceptions;
using WebhookKit.AspNetCore.Responses;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;

namespace WebhookKit.AspNetCore.Pipeline;

/// <summary>Coordinates raw-body capture, ingestion, queue admission, and safe response mapping.</summary>
public sealed class WebhookEndpointService : IWebhookEndpointService
{
    private readonly IWebhookBodyReader _bodyReader;
    private readonly WebhookIngestionService _ingestionService;
    private readonly IWebhookIdGenerator _idGenerator;
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IServiceProvider _services;
    private readonly WebhookResponseWriter _responseWriter;
    private readonly ILogger<WebhookEndpointService> _logger;

    /// <summary>Creates the endpoint service with its body, ingestion, ID, options, and response dependencies.</summary>
    /// <param name="bodyReader">Reader that captures the exact request body within the effective limit.</param>
    /// <param name="ingestionService">Verification, deduplication, and dispatch pipeline.</param>
    /// <param name="idGenerator">Generator for the transmission identifier.</param>
    /// <param name="options">Current WebhookKit options.</param>
    /// <param name="services">Request service provider used to resolve the queue.</param>
    /// <param name="responseWriter">Optional response writer; a default writer is created when omitted.</param>
    /// <param name="logger">Optional logger; a null logger uses a no-op logger.</param>
    public WebhookEndpointService(
        IWebhookBodyReader bodyReader,
        WebhookIngestionService ingestionService,
        IWebhookIdGenerator idGenerator,
        IOptions<WebhookKitOptions> options,
        IServiceProvider services,
        WebhookResponseWriter? responseWriter = null,
        ILogger<WebhookEndpointService>? logger = null)
    {
        _bodyReader = bodyReader ?? throw new ArgumentNullException(nameof(bodyReader));
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _responseWriter = responseWriter ?? new WebhookResponseWriter(new DefaultWebhookResponseFormatter());
        _logger = logger ?? NullLogger<WebhookEndpointService>.Instance;
    }

    /// <summary>Processes one webhook request and writes its safe response.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="options">Endpoint options; validated before request processing.</param>
    /// <param name="cancellationToken">Token used to cancel body reads, ingestion, queueing, and response writes.</param>
    /// <returns>The safe endpoint result.</returns>
    public async Task<WebhookEndpointResult> ProcessAsync(
        HttpContext context,
        WebhookEndpointOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();

        if (!_options.Value.Providers.TryGetValue(options.ProviderName, out var providerOptions))
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.", cancellationToken: cancellationToken);
        }

        var maxBodySize = providerOptions.MaxRequestBodySizeBytes ?? _options.Value.MaxRequestBodySizeBytes;
        if (maxBodySize <= 0)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.", cancellationToken: cancellationToken);
        }

        if (options.Mode == WebhookProcessingMode.Asynchronous && !_options.Value.Storage.PersistRawBody)
        {
            throw new WebhookConfigurationException("Asynchronous webhook processing requires raw body persistence.");
        }

        byte[] rawBody;
        try
        {
            rawBody = await _bodyReader
                .ReadRawBodyAsync(context, maxBodySize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebhookPayloadTooLargeException)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.PayloadTooLarge,
                StatusCodes.Status413PayloadTooLarge,
                "payload-too-large",
                "Webhook payload is too large.", cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "body-read-failed",
                "Webhook processing failed.", cancellationToken: cancellationToken);
        }

        string webhookId;
        try
        {
            webhookId = _idGenerator.Create();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "id-generation-failed",
                "Webhook processing failed.", cancellationToken: cancellationToken);
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> headers;
        try
        {
            headers = SnapshotHeaders(context.Request.Headers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "request-read-failed",
                "Webhook processing failed.", cancellationToken: cancellationToken, webhookId: webhookId);
        }

        var request = new WebhookIngestionRequest
        {
            WebhookId = webhookId,
            CorrelationId = GetCorrelationId(context),
            Provider = options.ProviderName,
            HttpMethod = string.IsNullOrWhiteSpace(context.Request.Method) ? HttpMethods.Post : context.Request.Method,
            RequestPath = context.Request.Path.Value ?? context.Request.PathBase.Value ?? "/",
            Headers = headers,
            RawBody = rawBody,
            ContentType = context.Request.ContentType,
            ContentLength = context.Request.ContentLength
        };

        if (options.Mode == WebhookProcessingMode.Asynchronous)
        {
            WebhookIngestionResult admission;
            try
            {
                admission = await _ingestionService.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return await CompleteAsync(
                    context,
                    options,
                    WebhookEndpointOutcome.ProcessingFailed,
                    StatusCodes.Status500InternalServerError,
                    "admission-failed",
                    "Webhook processing failed.", cancellationToken: cancellationToken, webhookId: webhookId);
            }
            if (admission.Status != WebhookIngestionStatus.Accepted)
            {
                return await MapIngestionResult(context, options, admission, webhookId, cancellationToken);
            }

            IWebhookQueue? queue;
            try
            {
                queue = _services.GetService<IWebhookQueue>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                queue = null;
            }

            if (queue is null)
            {
                return await CompleteAsync(
                    context,
                    options,
                    WebhookEndpointOutcome.QueueUnavailable,
                    StatusCodes.Status503ServiceUnavailable,
                    "queue-unavailable",
                    "Webhook processing is temporarily unavailable.", cancellationToken: cancellationToken, webhookId: webhookId);
            }

            try
            {
                var enqueued = await queue.TryEnqueueAsync(
                    new WebhookWorkItem(request.WebhookId, request.Provider),
                    cancellationToken).ConfigureAwait(false);
                if (!enqueued)
                {
                    return await CompleteAsync(
                        context,
                        options,
                        WebhookEndpointOutcome.QueueUnavailable,
                        StatusCodes.Status503ServiceUnavailable,
                        "queue-unavailable",
                        "Webhook processing is temporarily unavailable.",
                        cancellationToken: cancellationToken,
                        webhookId: webhookId);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return await CompleteAsync(
                    context,
                    options,
                    WebhookEndpointOutcome.QueueUnavailable,
                    StatusCodes.Status503ServiceUnavailable,
                    "queue-unavailable",
                    "Webhook processing is temporarily unavailable.", cancellationToken: cancellationToken, webhookId: webhookId);
            }

            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Accepted,
                StatusCodes.Status202Accepted,
                "accepted",
                "Webhook accepted.",
                admission.Context,
                cancellationToken: cancellationToken,
                webhookId: webhookId);
        }

        WebhookIngestionResult ingestionResult;
        try
        {
            ingestionResult = await _ingestionService.IngestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "processing-failed",
                "Webhook processing failed.", cancellationToken: cancellationToken, webhookId: webhookId);
        }

        return await MapIngestionResult(context, options, ingestionResult, webhookId, cancellationToken);
    }

    private Task<WebhookEndpointResult> MapIngestionResult(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result,
        string? webhookId,
        CancellationToken cancellationToken)
    {
        var asynchronous = options.Mode == WebhookProcessingMode.Asynchronous;
        return result.Status switch
        {
            WebhookIngestionStatus.Processed => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Processed,
                StatusCodes.Status200OK,
                "processed",
                "Webhook processed.",
                result.Context,
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            WebhookIngestionStatus.Accepted => CompleteAsync(
                context,
                options,
                asynchronous ? WebhookEndpointOutcome.Accepted : WebhookEndpointOutcome.Processed,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                asynchronous ? "accepted" : "processed",
                asynchronous ? "Webhook accepted." : "Webhook processed.",
                result.Context,
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            WebhookIngestionStatus.Duplicate => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Duplicate,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "duplicate",
                "Webhook already received.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            WebhookIngestionStatus.Ignored => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Ignored,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "ignored",
                "Webhook ignored.",
                result.Context,
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            WebhookIngestionStatus.Rejected => MapRejected(context, options, result.FailureCode, webhookId, cancellationToken),
            WebhookIngestionStatus.Failed => MapFailure(context, options, result, webhookId, cancellationToken),
            _ => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "processing-failed",
                "Webhook processing failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId)
        };
    }

    private Task<WebhookEndpointResult> MapRejected(
        HttpContext context,
        WebhookEndpointOptions options,
        string? failureCode,
        string? webhookId,
        CancellationToken cancellationToken)
    {
        return failureCode == "signature-verification-failed"
            ? CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidSignature,
                StatusCodes.Status401Unauthorized,
                "signature-verification-failed",
                "Webhook signature verification failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId)
            : CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId);
    }

    private Task<WebhookEndpointResult> MapFailure(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result,
        string? webhookId,
        CancellationToken cancellationToken)
    {
        return result.FailureCode switch
        {
            "event-id-required" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.MissingEventId,
                StatusCodes.Status400BadRequest,
                "event-id-required",
                "Webhook event identifier is required.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            "missing-event-type" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.MissingEventType,
                StatusCodes.Status400BadRequest,
                "missing-event-type",
                "Webhook event type is required.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            "event-id-extraction-failed" or "event-type-extraction-failed" or "payload-invalid" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.PayloadInvalid,
                StatusCodes.Status400BadRequest,
                "payload-invalid",
                "Webhook payload is invalid.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            "signature-verification-failed" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidSignature,
                StatusCodes.Status401Unauthorized,
                "signature-verification-failed",
                "Webhook signature verification failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            "timestamp-verification-failed" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            "provider-not-configured" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.",
                cancellationToken: cancellationToken,
                webhookId: webhookId),
            _ => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "handler-failed",
                "Webhook processing failed.",
                cancellationToken: cancellationToken,
                webhookId: webhookId)
        };
    }

    private async Task<WebhookEndpointResult> CompleteAsync(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookEndpointOutcome outcome,
        int defaultStatusCode,
        string code,
        string message,
        WebhookContext? webhookContext = null,
        string? webhookId = null,
        CancellationToken cancellationToken = default)
    {
        var result = WebhookEndpointResult.Create(
            context,
            outcome,
            defaultStatusCode,
            code,
            message,
            webhookContext,
            options);
        var logWebhookId = webhookContext?.WebhookId ?? webhookId;
        var logEventId = webhookContext?.EventId;
        var logEventType = webhookContext?.EventType;
        var logCorrelationId = webhookContext?.CorrelationId ?? GetCorrelationId(context);
        var logOutcome = result.Outcome.ToString();
        var logMode = options.Mode.ToString();
        var logFailureCode = result.IsSuccess ? null : result.Code;
        switch (result.Outcome)
        {
            case WebhookEndpointOutcome.Processed:
            case WebhookEndpointOutcome.Accepted:
            case WebhookEndpointOutcome.Duplicate:
            case WebhookEndpointOutcome.Ignored:
                WebhookEndpointLogMessages.Completed(
                    _logger,
                    options.ProviderName,
                    logWebhookId,
                    logEventId,
                    logEventType,
                    result.StatusCode,
                    logOutcome,
                    logMode,
                    result.TraceId,
                    logCorrelationId,
                    logFailureCode);
                break;
            case WebhookEndpointOutcome.InvalidSignature:
            case WebhookEndpointOutcome.InvalidTimestamp:
            case WebhookEndpointOutcome.MissingEventId:
            case WebhookEndpointOutcome.MissingEventType:
            case WebhookEndpointOutcome.PayloadInvalid:
            case WebhookEndpointOutcome.PayloadTooLarge:
            case WebhookEndpointOutcome.QueueUnavailable:
                WebhookEndpointLogMessages.Rejected(
                    _logger,
                    options.ProviderName,
                    logWebhookId,
                    logEventId,
                    logEventType,
                    result.StatusCode,
                    logOutcome,
                    logMode,
                    result.TraceId,
                    logCorrelationId,
                    logFailureCode);
                break;
            default:
                WebhookEndpointLogMessages.Failed(
                    _logger,
                    options.ProviderName,
                    logWebhookId,
                    logEventId,
                    logEventType,
                    result.StatusCode,
                    logOutcome,
                    logMode,
                    result.TraceId,
                    logCorrelationId,
                    logFailureCode);
                break;
        }

        await _responseWriter.WriteAsync(context, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string? GetCorrelationId(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString();
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId;
        }

        return string.IsNullOrWhiteSpace(context.TraceIdentifier) ? null : context.TraceIdentifier;
    }

    private static ReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotHeaders(IHeaderDictionary headers)
    {
        var snapshot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            snapshot[header.Key] = Array.AsReadOnly(
                header.Value.Select(value => value ?? string.Empty).ToArray());
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(snapshot);
    }
}
