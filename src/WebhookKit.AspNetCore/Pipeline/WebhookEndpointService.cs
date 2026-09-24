using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Exceptions;
using WebhookKit.AspNetCore.Responses;
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
    private readonly WebhookResponseWriter _responseWriter;

    public WebhookEndpointService(
        IWebhookBodyReader bodyReader,
        WebhookIngestionService ingestionService,
        IWebhookIdGenerator idGenerator,
        IOptions<WebhookKitOptions> options,
        IServiceProvider services,
        WebhookResponseWriter? responseWriter = null)
    {
        _bodyReader = bodyReader ?? throw new ArgumentNullException(nameof(bodyReader));
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _responseWriter = responseWriter ?? new WebhookResponseWriter(new DefaultWebhookResponseFormatter());
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
            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "request-read-failed",
                "Webhook processing failed.", cancellationToken: cancellationToken);
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
                return await CompleteAsync(
                    context,
                    options,
                    WebhookEndpointOutcome.ProcessingFailed,
                    StatusCodes.Status500InternalServerError,
                    "admission-failed",
                    "Webhook processing failed.", cancellationToken: cancellationToken);
            }
            if (admission.Status != WebhookIngestionStatus.Accepted)
            {
                return await MapIngestionResult(context, options, admission, cancellationToken);
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
                    "Webhook processing is temporarily unavailable.", cancellationToken: cancellationToken);
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
                        "Webhook processing is temporarily unavailable.", cancellationToken: cancellationToken);
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
                    "Webhook processing is temporarily unavailable.", cancellationToken: cancellationToken);
            }

            return await CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Accepted,
                StatusCodes.Status202Accepted,
                "accepted",
                "Webhook accepted.",
                admission.Context,
                cancellationToken: cancellationToken);
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
                "Webhook processing failed.", cancellationToken: cancellationToken);
        }

        return await MapIngestionResult(context, options, ingestionResult, cancellationToken);
    }

    private Task<WebhookEndpointResult> MapIngestionResult(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result,
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
                cancellationToken: cancellationToken),
            WebhookIngestionStatus.Accepted => CompleteAsync(
                context,
                options,
                asynchronous ? WebhookEndpointOutcome.Accepted : WebhookEndpointOutcome.Processed,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                asynchronous ? "accepted" : "processed",
                asynchronous ? "Webhook accepted." : "Webhook processed.",
                result.Context,
                cancellationToken: cancellationToken),
            WebhookIngestionStatus.Duplicate => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Duplicate,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "duplicate",
                "Webhook already received.",
                cancellationToken: cancellationToken),
            WebhookIngestionStatus.Ignored => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.Ignored,
                asynchronous ? StatusCodes.Status202Accepted : StatusCodes.Status200OK,
                "ignored",
                "Webhook ignored.",
                result.Context,
                cancellationToken: cancellationToken),
            WebhookIngestionStatus.Rejected => MapRejected(context, options, result.FailureCode, cancellationToken),
            WebhookIngestionStatus.Failed => MapFailure(context, options, result, cancellationToken),
            _ => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "processing-failed",
                "Webhook processing failed.",
                cancellationToken: cancellationToken)
        };
    }

    private Task<WebhookEndpointResult> MapRejected(
        HttpContext context,
        WebhookEndpointOptions options,
        string? failureCode,
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
                cancellationToken: cancellationToken)
            : CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed.",
                cancellationToken: cancellationToken);
    }

    private Task<WebhookEndpointResult> MapFailure(
        HttpContext context,
        WebhookEndpointOptions options,
        WebhookIngestionResult result,
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
                cancellationToken: cancellationToken),
            "missing-event-type" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.MissingEventType,
                StatusCodes.Status400BadRequest,
                "missing-event-type",
                "Webhook event type is required.",
                cancellationToken: cancellationToken),
            "event-id-extraction-failed" or "event-type-extraction-failed" or "payload-invalid" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.PayloadInvalid,
                StatusCodes.Status400BadRequest,
                "payload-invalid",
                "Webhook payload is invalid.",
                cancellationToken: cancellationToken),
            "signature-verification-failed" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidSignature,
                StatusCodes.Status401Unauthorized,
                "signature-verification-failed",
                "Webhook signature verification failed.",
                cancellationToken: cancellationToken),
            "timestamp-verification-failed" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.InvalidTimestamp,
                StatusCodes.Status400BadRequest,
                "timestamp-verification-failed",
                "Webhook timestamp verification failed.",
                cancellationToken: cancellationToken),
            "provider-not-configured" => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ConfigurationError,
                StatusCodes.Status500InternalServerError,
                "webhook-configuration-error",
                "Webhook processing is not configured.",
                cancellationToken: cancellationToken),
            _ => CompleteAsync(
                context,
                options,
                WebhookEndpointOutcome.ProcessingFailed,
                StatusCodes.Status500InternalServerError,
                "handler-failed",
                "Webhook processing failed.",
                cancellationToken: cancellationToken)
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
        await _responseWriter.WriteAsync(context, result, cancellationToken).ConfigureAwait(false);
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
