using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Queues;

/// <summary>Bounded in-process queue used by the default asynchronous processing path.</summary>
/// <remarks>The channel is process-local and is not a durable broker; rejected enqueue attempts leave the persisted delivery recoverable.</remarks>
[SuppressMessage("Naming", "CA1711", Justification = "The queue name is part of the concrete default registration.")]
internal sealed class ChannelWebhookQueue : IWebhookQueue
{
    private readonly Channel<WebhookWorkItem> _channel;

    /// <summary>Creates a bounded channel with the configured capacity.</summary>
    /// <param name="options">Options supplying the queue capacity.</param>
    public ChannelWebhookQueue(IOptions<WebhookKitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = Channel.CreateBounded<WebhookWorkItem>(new BoundedChannelOptions(options.Value.Queue.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Attempts to write a work item without waiting for capacity.</summary>
    /// <param name="workItem">The delivery reference to enqueue.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when the item was accepted.</returns>
    public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_channel.Writer.TryWrite(workItem));
    }

    /// <summary>Waits for the next work item.</summary>
    /// <param name="cancellationToken">Token used to cancel waiting.</param>
    /// <returns>The next work item.</returns>
    public async ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the channel writer.</summary>
    /// <param name="cancellationToken">Token used to cancel completion.</param>
    /// <returns>A completed task.</returns>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
