using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Diagnostics;
using WebhookKit.Core.Handlers;

namespace WebhookKit.Core.Processing;

/// <summary>Dispatches a verified context and returns a safe dispatch result.</summary>
public interface IWebhookDispatchProcessor
{
    /// <summary>Dispatches the context to handlers registered for its event type.</summary>
    /// <param name="context">The verified execution context.</param>
    /// <param name="cancellationToken">Token used to cancel dispatch.</param>
    /// <returns>The dispatch result.</returns>
    Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default);
}

/// <summary>Dispatches matching handlers sequentially using a scoped service provider.</summary>
internal sealed class WebhookProcessor : IWebhookDispatchProcessor
{
    private readonly WebhookHandlerRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookProcessor> _logger;

    /// <summary>Creates a processor with a handler registry and scope factory.</summary>
    /// <param name="registry">Registered handler descriptors.</param>
    /// <param name="scopeFactory">Factory used to create one DI scope per dispatch.</param>
    /// <param name="logger">Optional logger; a null logger uses a no-op logger.</param>
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

    /// <summary>Dispatches matching handlers sequentially in registration order.</summary>
    /// <param name="context">The verified context; it must contain an event type.</param>
    /// <param name="cancellationToken">Token used to cancel dispatch and handler execution.</param>
    /// <returns>A processed, ignored, or failed result with safe failure codes.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
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

}
