using WebhookKit.Abstractions;

namespace WebhookKit.Core.Deduplication;

public sealed class DefaultWebhookDeduplicator : IWebhookDeduplicator
{
    private readonly IWebhookStore _store;

    public DefaultWebhookDeduplicator(IWebhookStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public ValueTask<bool> TryAcquireAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        return _store.TryCreateAsync(record, cancellationToken);
    }
}
