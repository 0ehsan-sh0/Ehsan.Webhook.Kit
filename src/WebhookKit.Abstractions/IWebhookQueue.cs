using System.Diagnostics.CodeAnalysis;

namespace WebhookKit.Abstractions;

[SuppressMessage("Naming", "CA1711", Justification = "The queue name is part of the public provider-neutral contract.")]
public interface IWebhookQueue
{
    ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default);

    ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
