// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Extracts the provider event identifier from headers or payload bytes.
/// Returns <c>null</c> when no identifier can be found.
/// </summary>
public interface IWebhookEventIdExtractor
{
    /// <summary>Extract the event ID, or <c>null</c> when absent.</summary>
    ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default);
}
