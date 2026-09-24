using Microsoft.AspNetCore.Http;

namespace WebhookKit.AspNetCore.Pipeline;

public interface IWebhookEndpointService
{
    Task<WebhookEndpointResult> ProcessAsync(
        HttpContext context,
        WebhookEndpointOptions options,
        CancellationToken cancellationToken = default);
}
