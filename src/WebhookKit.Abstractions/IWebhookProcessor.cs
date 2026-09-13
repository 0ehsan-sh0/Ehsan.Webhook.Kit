// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Processes an already-verified, already-deduplicated <see cref="WebhookContext"/>.
/// Verification and deduplication happen upstream; this owns handler dispatch only.
/// </summary>
public interface IWebhookProcessor
{
    /// <summary>Dispatch the context to the matching handler(s).</summary>
    Task ProcessAsync(WebhookContext context, CancellationToken cancellationToken = default);
}
