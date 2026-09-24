using System.Diagnostics.CodeAnalysis;

namespace WebhookKit.Abstractions;

/// <summary>Accepts and delivers references to admitted webhook work.</summary>
/// <remarks>Queue implementations define their durability and shutdown behavior; callers must not treat this contract as a durable broker guarantee.</remarks>
[SuppressMessage("Naming", "CA1711", Justification = "The queue name is part of the public provider-neutral contract.")]
public interface IWebhookQueue
{
    /// <summary>Attempts to enqueue one work item without waiting for capacity.</summary>
    /// <param name="workItem">The admitted delivery reference.</param>
    /// <param name="cancellationToken">Token used to cancel the enqueue operation.</param>
    /// <returns><see langword="true"/> when accepted; otherwise <see langword="false"/> when the queue cannot accept it.</returns>
    ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default);

    /// <summary>Waits for the next work item.</summary>
    /// <param name="cancellationToken">Token used to cancel waiting and delivery.</param>
    /// <returns>The next queued work item.</returns>
    ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Signals that no more work will be enqueued.</summary>
    /// <param name="cancellationToken">Token used to cancel completion.</param>
    /// <returns>A task that completes when the queue accepts the completion signal.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
