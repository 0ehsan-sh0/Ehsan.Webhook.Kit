using WebhookKit.Abstractions;

namespace WebhookKit.Core.Deduplication;

/// <summary>Uses an <see cref="IWebhookStore"/> to atomically claim a delivery.</summary>
internal sealed class DefaultWebhookDeduplicator : IWebhookDeduplicator
{
    private readonly IWebhookStore _store;

    /// <summary>Creates a deduplicator backed by the supplied store.</summary>
    /// <param name="store">The persistence store used for atomic insertion.</param>
    public DefaultWebhookDeduplicator(IWebhookStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Attempts to acquire the record's deduplication key.</summary>
    /// <param name="record">The record to insert.</param>
    /// <param name="cancellationToken">Token used to cancel the store operation.</param>
    /// <returns><see langword="true"/> for the first delivery; otherwise <see langword="false"/>.</returns>
    public ValueTask<bool> TryAcquireAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        return _store.TryCreateAsync(record, cancellationToken);
    }
}
