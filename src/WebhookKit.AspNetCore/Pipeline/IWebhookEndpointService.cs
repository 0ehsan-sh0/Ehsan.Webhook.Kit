using Microsoft.AspNetCore.Http;

namespace WebhookKit.AspNetCore.Pipeline;

/// <summary>Processes an authenticated HTTP webhook request through the configured pipeline.</summary>
public interface IWebhookEndpointService
{
    /// <summary>Processes the current request using the supplied endpoint snapshot.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="options">Endpoint options snapshot.</param>
    /// <param name="cancellationToken">Token used to cancel request processing.</param>
    /// <returns>A safe endpoint result suitable for response mapping.</returns>
    Task<WebhookEndpointResult> ProcessAsync(
        HttpContext context,
        WebhookEndpointOptions options,
        CancellationToken cancellationToken = default);
}
