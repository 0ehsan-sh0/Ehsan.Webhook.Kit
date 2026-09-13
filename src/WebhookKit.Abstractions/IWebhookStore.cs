// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Abstractions;

/// <summary>
/// Persistence abstraction for webhook records. Implementations must perform
/// <see cref="TryCreateAsync"/> atomically: concurrent inserts for the same
/// {Provider, EventId} must yield exactly one success.
/// Contracts are strict: <paramref name="provider"/> and <paramref name="eventId"/>
/// must be non-empty. Null-EventId hash fallback lives in the deduplication
/// pipeline (Task 11), not here.
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
}
