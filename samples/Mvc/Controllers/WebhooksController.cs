using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Mvc;

namespace MvcSample.Controllers;

[ApiController]
[Route("webhooks")]
public sealed class WebhooksController : ControllerBase
{
    [HttpPost("sample")]
    [WebhookEndpoint("sample-provider")]
    public IActionResult Receive(WebhookContext context)
    {
        _ = context.GetPayload<OrderCreated>();
        return Ok();
    }
}

public sealed record OrderCreated(string OrderId, string CustomerId, decimal Amount);

public sealed class OrderCreatedHandler(ILogger<OrderCreatedHandler> logger) : IWebhookHandler<OrderCreated>
{
    private static readonly Action<ILogger, string, string?, string?, Exception?> LogHandled =
        LoggerMessage.Define<string, string?, string?>(
            LogLevel.Information,
            new EventId(1, "SampleWebhookHandled"),
            "Handled webhook for provider {Provider}, event {EventId}, type {EventType}.");

    public Task HandleAsync(OrderCreated eventData, WebhookContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogHandled(logger, context.Provider, context.EventId, context.EventType, null);
        return Task.CompletedTask;
    }
}
