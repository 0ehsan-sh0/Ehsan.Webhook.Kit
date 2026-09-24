// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Processes an already-verified, already-deduplicated <see cref="WebhookContext"/>.
/// Verification and deduplication happen upstream; this owns handler dispatch only.
/// </summary>
public interface IWebhookProcessor
{
    /// <summary>Dispatch the context to the matching handler(s).</summary>
    /// <param name="context">Verified execution context; verification and deduplication have already completed.</param>
    /// <param name="cancellationToken">Token used to cancel dispatch.</param>
    /// <returns>A task that represents handler dispatch.</returns>
    Task ProcessAsync(WebhookContext context, CancellationToken cancellationToken = default);
}
