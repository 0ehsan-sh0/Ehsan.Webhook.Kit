using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
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

    public WebhookProcessor(WebhookHandlerRegistry registry, IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _registry = registry;
        _scopeFactory = scopeFactory;
    }

    public async Task<WebhookDispatchResult> DispatchAsync(
        WebhookContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.EventType))
        {
            return WebhookDispatchResult.Failed(
                WebhookDispatchFailureKind.Payload,
                "missing-event-type",
                new WebhookPayloadException());
        }

        var handlers = _registry.GetHandlers(context.EventType);
        await using var scope = _scopeFactory.CreateAsyncScope();
        if (handlers.Count == 0)
        {
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
            return WebhookDispatchResult.Processed();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebhookPayloadException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (WebhookPayloadException exception)
        {
            return WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Payload, "payload-invalid", exception);
        }
        catch (Exception exception)
        {
            return WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed", exception);
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
