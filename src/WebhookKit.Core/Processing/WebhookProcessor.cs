using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Diagnostics;
using WebhookKit.Core.Handlers;

namespace WebhookKit.Core.Processing;

public interface IWebhookDispatchProcessor
{
    Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default);
}

public sealed class WebhookProcessor : IWebhookProcessor, IWebhookDispatchProcessor
{
    private readonly WebhookHandlerRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookProcessor> _logger;

    public WebhookProcessor(
        WebhookHandlerRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _registry = registry;
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<WebhookProcessor>.Instance;
    }

    public async Task<WebhookDispatchResult> DispatchAsync(
        WebhookContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var traceId = Activity.Current?.TraceId.ToString();
        using var activity = WebhookDiagnostics.StartProcess(
            context.WebhookId,
            context.Provider,
            context.EventId,
            context.EventType,
            context.CorrelationId);

        try
        {
            WebhookLogMessages.Processing(
                _logger,
                context.WebhookId,
                context.Provider,
                context.EventId,
                context.EventType,
                nameof(WebhookProcessingStatus.Processing),
                0,
                traceId,
                context.CorrelationId);

            if (string.IsNullOrWhiteSpace(context.EventType))
            {
                WebhookLogMessages.Failed(
                    _logger,
                    context.WebhookId,
                    context.Provider,
                    context.EventId,
                    null,
                    nameof(WebhookProcessingStatus.Failed),
                    0,
                    traceId,
                    context.CorrelationId,
                    "missing-event-type");
                WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Failed), true);
                return WebhookDispatchResult.Failed(
                    WebhookDispatchFailureKind.Payload,
                    "missing-event-type",
                    new WebhookPayloadException());
            }

            var handlers = _registry.GetHandlers(context.EventType);
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (handlers.Count == 0)
            {
                WebhookLogMessages.Ignored(
                    _logger,
                    context.WebhookId,
                    context.Provider,
                    context.EventId,
                    context.EventType,
                    nameof(WebhookProcessingStatus.Ignored),
                    0,
                    traceId,
                    context.CorrelationId);
                WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Ignored), false);
                return WebhookDispatchResult.Ignored();
            }

            try
            {
                foreach (var handler in handlers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await handler.InvokeAsync(scope.ServiceProvider, context, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                WebhookLogMessages.Processed(
                    _logger,
                    context.WebhookId,
                    context.Provider,
                    context.EventId,
                    context.EventType,
                    nameof(WebhookProcessingStatus.Processed),
                    0,
                    traceId,
                    context.CorrelationId);
                WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Processed), false);
                return WebhookDispatchResult.Processed();
            }
            catch (OperationCanceledException)
            {
                WebhookDiagnostics.SetResult(activity, "Cancelled", true);
                throw;
            }
            catch (WebhookPayloadException) when (cancellationToken.IsCancellationRequested)
            {
                WebhookDiagnostics.SetResult(activity, "Cancelled", true);
                throw new OperationCanceledException(cancellationToken);
            }
            catch (WebhookPayloadException exception)
            {
                WebhookLogMessages.Failed(
                    _logger,
                    context.WebhookId,
                    context.Provider,
                    context.EventId,
                    context.EventType,
                    nameof(WebhookProcessingStatus.Failed),
                    0,
                    traceId,
                    context.CorrelationId,
                    "payload-invalid");
                WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Failed), true);
                return WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Payload, "payload-invalid", exception);
            }
            catch (Exception exception)
            {
                WebhookLogMessages.Failed(
                    _logger,
                    context.WebhookId,
                    context.Provider,
                    context.EventId,
                    context.EventType,
                    nameof(WebhookProcessingStatus.Failed),
                    0,
                    traceId,
                    context.CorrelationId,
                    "handler-failed");
                WebhookDiagnostics.SetResult(activity, nameof(WebhookProcessingStatus.Failed), true);
                return WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed", exception);
            }
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

    public async Task ProcessAsync(WebhookContext context, CancellationToken cancellationToken = default)
    {
        var result = await DispatchAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Status != WebhookDispatchStatus.Failed)
        {
            return;
        }

        if (result.FailureException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.FailureException).Throw();
        }

        throw new InvalidOperationException("Webhook processing failed.");
    }
}
