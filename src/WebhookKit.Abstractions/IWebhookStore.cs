// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Persistence abstraction for webhook records. Implementations must perform
/// <see cref="TryCreateAsync"/> atomically for the provider-scoped
/// <see cref="WebhookRecord.DeduplicationKey"/>.
/// </summary>
public interface IWebhookStore
{
    /// <summary>Looks up a record by provider and event identifier.</summary>
    /// <param name="provider">Configured provider name.</param>
    /// <param name="eventId">Provider event identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the lookup.</param>
    /// <returns>A detached record snapshot, or <see langword="null"/> when absent.</returns>
    ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts a record if its deduplication key is not already stored.</summary>
    /// <param name="record">The record to store; implementations may snapshot owned collections.</param>
    /// <param name="cancellationToken">Token used to cancel insertion.</param>
    /// <returns><see langword="true"/> when inserted; <see langword="false"/> for an existing key.</returns>
    ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default);

    /// <summary>Persists status and attempt mutations for an existing record.</summary>
    /// <param name="record">The updated record snapshot.</param>
    /// <param name="cancellationToken">Token used to cancel persistence.</param>
    /// <returns>A task that completes when the update is applied or ignored by the store's concurrency rules.</returns>
    ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default);

    /// <summary>Looks up a record by its WebhookKit transmission identifier.</summary>
    /// <param name="webhookId">The transmission identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the lookup.</param>
    /// <returns>A detached record snapshot, or <see langword="null"/> when absent.</returns>
    ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default);

    /// <summary>Attempts to acquire a processing lease for a received or expired record.</summary>
    /// <param name="webhookId">The transmission identifier.</param>
    /// <param name="leaseOwner">The caller's processing lease owner.</param>
    /// <param name="leaseDuration">Positive lease duration.</param>
    /// <param name="cancellationToken">Token used to cancel the claim.</param>
    /// <returns><see langword="true"/> when the lease was acquired.</returns>
    ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Releases an owned processing lease and returns the record to the received state.</summary>
    /// <param name="webhookId">The transmission identifier.</param>
    /// <param name="leaseOwner">The current processing lease owner.</param>
    /// <param name="cancellationToken">Token used to cancel release.</param>
    /// <returns><see langword="true"/> when the owned lease was released.</returns>
    ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default);

    /// <summary>Marks an owned processing record as successfully processed.</summary>
    /// <param name="webhookId">The transmission identifier.</param>
    /// <param name="leaseOwner">The current processing lease owner.</param>
    /// <param name="processedAt">Completion time supplied by the worker clock.</param>
    /// <param name="cancellationToken">Token used to cancel the update.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
    ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default);

    /// <summary>Marks an owned processing record as failed using a safe diagnostic reason.</summary>
    /// <param name="webhookId">The transmission identifier.</param>
    /// <param name="leaseOwner">The current processing lease owner.</param>
    /// <param name="failedAt">Failure time supplied by the worker clock.</param>
    /// <param name="failureReason">A safe code-like reason; secrets and raw data are not accepted by store implementations.</param>
    /// <param name="cancellationToken">Token used to cancel the update.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
    ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default);

    /// <summary>Gets received records and expired processing leases for recovery.</summary>
    /// <param name="now">Current time used to determine lease expiry.</param>
    /// <param name="expiredLeaseAge">Minimum age for an expired lease to be recoverable.</param>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <param name="cancellationToken">Token used to cancel recovery.</param>
    /// <returns>Detached record snapshots ordered by the store's recovery policy.</returns>
    ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default);
}
