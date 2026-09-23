// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Persistence abstraction for webhook records. Implementations must perform
/// <see cref="TryCreateAsync"/> atomically for the provider-scoped
/// <see cref="WebhookRecord.DeduplicationKey"/>.
/// </summary>
public interface IWebhookStore
{
    /// <summary>Lookup by composite deduplication key.</summary>
    ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically insert. Returns <c>true</c> when created, <c>false</c> when the key already exists.
    /// </summary>
    ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default);

    /// <summary>Persist status/attempt mutations for an existing record.</summary>
    ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default);

    ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default);

    ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default);

    ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default);

    ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default);
}
