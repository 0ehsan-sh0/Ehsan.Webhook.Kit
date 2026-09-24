// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Extracts the categorical event type from headers or payload bytes.
/// Returns <c>null</c> when no type can be found. Event types are opaque strings;
/// the library never imposes an enum.
/// </summary>
public interface IWebhookEventTypeExtractor
{
    /// <summary>Extract the event type, or <c>null</c> when absent.</summary>
    /// <param name="context">Provider name, exact body bytes, and headers; it contains no secret.</param>
    /// <param name="cancellationToken">Token used to cancel extraction.</param>
    /// <returns>The extracted event type, or <see langword="null"/>.</returns>
    ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default);
}
