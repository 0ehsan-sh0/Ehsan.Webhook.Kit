using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Exceptions;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;

namespace WebhookKit.AspNetCore.Pipeline;

public sealed class WebhookEndpointService : IWebhookEndpointService
{
    private readonly IWebhookBodyReader _bodyReader;
    private readonly WebhookIngestionService _ingestionService;
    private readonly IWebhookIdGenerator _idGenerator;
    private readonly IOptions<WebhookKitOptions> _options;
    private readonly IServiceProvider _services;

    public WebhookEndpointService(
        IWebhookBodyReader bodyReader,
        WebhookIngestionService ingestionService,
        IWebhookIdGenerator idGenerator,
        IOptions<WebhookKitOptions> options,
        IServiceProvider services)
    {
        _bodyReader = bodyReader ?? throw new ArgumentNullException(nameof(bodyReader));
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

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
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.");
        }

        var maxBodySize = providerOptions.MaxRequestBodySizeBytes ?? _options.Value.MaxRequestBodySizeBytes;
        if (maxBodySize <= 0)
        {
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.");
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
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.PayloadTooLarge,
                StatusCodes.Status413PayloadTooLarge,
                "payload-too-large",
                "Webhook payload is too large.");
        }
        catch (Exception)
        {
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "body-read-failed",
                "Webhook processing failed.");
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
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "id-generation-failed",
                "Webhook processing failed.");
        }

        IReadOnlyDictionary<string, string[]> headers;
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
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "request-read-failed",
                "Webhook processing failed.");
        }

        var request = new WebhookIngestionRequest
        {
            WebhookId = webhookId,
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
                return Complete(
                    context,
                    options,
                    WebhookEndpointOutcome.ProcessingFailed,
                    StatusCodes.Status500InternalServerError,
                    "admission-failed",
                    "Webhook processing failed.");
            }
            if (admission.Status != WebhookIngestionStatus.Accepted)
            {
                return MapIngestionResult(context, options, admission);
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
                return Complete(
                    context,
                    options,
                    WebhookEndpointOutcome.QueueUnavailable,
                    StatusCodes.Status503ServiceUnavailable,
                    "queue-unavailable",
                    "Webhook processing is temporarily unavailable.");
            }

            try
            {
                var enqueued = await queue.TryEnqueueAsync(
                    new WebhookWorkItem(request.WebhookId, request.Provider),
                    cancellationToken).ConfigureAwait(false);
                if (!enqueued)
                {
                    return Complete(
                        context,
                        options,
                        WebhookEndpointOutcome.QueueUnavailable,
                        StatusCodes.Status503ServiceUnavailable,
                        "queue-unavailable",
                        "Webhook processing is temporarily unavailable.");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return Complete(
                    context,
                    options,
                    WebhookEndpointOutcome.QueueUnavailable,
                    StatusCodes.Status503ServiceUnavailable,
                    "queue-unavailable",
                    "Webhook processing is temporarily unavailable.");
            }

            return Complete(
                context,
                options,
                WebhookEndpointOutcome.Accepted,
                StatusCodes.Status202Accepted,
                "accepted",
                "Webhook accepted.",
                admission.Context);
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
            return Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "processing-failed",
                "Webhook processing failed.");
        }

        return MapIngestionResult(context, options, ingestionResult);
    }

    private static WebhookEndpointResult MapIngestionResult(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result)
    {
        var asynchronous = options.Mode == WebhookProcessingMode.Asynchronous;
        return result.Status switch
        {
            WebhookIngestionStatus.Processed => Complete(
                context,
                options,
                WebhookEndpointOutcome.Processed,
                StatusCodes.Status200OK,
                "processed",
                "Webhook processed.",
                result.Context),
            WebhookIngestionStatus.Accepted => Complete(
                context,
                options,
                asynchronous ? WebhookEndpointOutcome.Accepted : WebhookEndpointOutcome.Processed,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                asynchronous ? "accepted" : "processed",
                asynchronous ? "Webhook accepted." : "Webhook processed.",
                result.Context),
            WebhookIngestionStatus.Duplicate => Complete(
                context,
                options,
                WebhookEndpointOutcome.Duplicate,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "duplicate",
                "Webhook already received."),
            WebhookIngestionStatus.Ignored => Complete(
                context,
                options,
                WebhookEndpointOutcome.Ignored,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "ignored",
                "Webhook ignored.",
                result.Context),
            WebhookIngestionStatus.Rejected => MapRejected(context, options, result.FailureCode),
            WebhookIngestionStatus.Failed => MapFailure(context, options, result),
            _ => Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "processing-failed",
                "Webhook processing failed.")
        };
    }

    private static WebhookEndpointResult MapRejected(
        HttpContext context,
        WebhookEndpointOptions options,
        string? failureCode)
    {
        return failureCode == "signature-verification-failed"
            ? Complete(
                context,
                options,
                WebhookEndpointOutcome.InvalidSignature,
                StatusCodes.Status401Unauthorized,
                "signature-verification-failed",
                "Webhook signature verification failed.")
            : Complete(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed.");
    }

    private static WebhookEndpointResult MapFailure(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result)
    {
        return result.FailureCode switch
        {
            "event-id-required" => Complete(
                context,
                options,
                WebhookEndpointOutcome.MissingEventId,
                StatusCodes.Status400BadRequest,
                "event-id-required",
                "Webhook event identifier is required."),
            "missing-event-type" => Complete(
                context,
                options,
                WebhookEndpointOutcome.MissingEventType,
                StatusCodes.Status400BadRequest,
                "missing-event-type",
                "Webhook event type is required."),
            "event-id-extraction-failed" or "event-type-extraction-failed" or "payload-invalid" => Complete(
                context,
                options,
                WebhookEndpointOutcome.PayloadInvalid,
                StatusCodes.Status400BadRequest,
                "payload-invalid",
                "Webhook payload is invalid."),
            "signature-verification-failed" => Complete(
                context,
                options,
                WebhookEndpointOutcome.InvalidSignature,
                StatusCodes.Status401Unauthorized,
                "signature-verification-failed",
                "Webhook signature verification failed."),
            "timestamp-verification-failed" => Complete(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed."),
            "provider-not-configured" => Complete(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured."),
            _ => Complete(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "handler-failed",
                "Webhook processing failed.")
        };
    }

    private static WebhookEndpointResult Complete(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookEndpointOutcome outcome,
        int defaultStatusCode,
        string code,
        string message,
        WebhookContext? webhookContext = null)
    {
        var result = WebhookEndpointResult.Create(
            context,
            outcome,
            defaultStatusCode,
            code,
            message,
            webhookContext,
            options);
        context.Response.StatusCode = result.StatusCode;
        return result;
    }

    private static ReadOnlyDictionary<string, string[]> SnapshotHeaders(IHeaderDictionary headers)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            snapshot[header.Key] = header.Value.Select(value => value ?? string.Empty).ToArray();
        }

        return new ReadOnlyDictionary<string, string[]>(snapshot);
    }
}
