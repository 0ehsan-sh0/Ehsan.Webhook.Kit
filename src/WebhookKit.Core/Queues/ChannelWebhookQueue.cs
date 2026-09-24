using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;

namespace WebhookKit.Core.Queues;

[SuppressMessage("Naming", "CA1711", Justification = "The queue name is part of the concrete default registration.")]
public sealed class ChannelWebhookQueue : IWebhookQueue
{
    private readonly Channel<WebhookWorkItem> _channel;

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

    public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_channel.Writer.TryWrite(workItem));
    }

    public async ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
