using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Queues;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class ChannelWebhookQueueTests
{
    [Fact]
    public async Task Queue_DequeuesInFifoOrder()
    {
        var queue = CreateQueue(capacity: 3);
        var first = new WebhookWorkItem("webhook-1", "payments");
        var second = new WebhookWorkItem("webhook-2", "payments");
        var third = new WebhookWorkItem("webhook-3", "billing");

        (await queue.TryEnqueueAsync(first)).Should().BeTrue();
        (await queue.TryEnqueueAsync(second)).Should().BeTrue();
        (await queue.TryEnqueueAsync(third)).Should().BeTrue();

        var dequeued = new[]
        {
            await queue.DequeueAsync(),
            await queue.DequeueAsync(),
            await queue.DequeueAsync()
        };

        dequeued.Should().Equal(first, second, third);
    }

    [Fact]
    public async Task DequeueAsync_WaitsForNextItemWithoutPolling()
    {
        var queue = CreateQueue(capacity: 1);
        var expected = new WebhookWorkItem("webhook-waiting", "payments");

        var pending = queue.DequeueAsync().AsTask();
        pending.IsCompleted.Should().BeFalse();

        (await queue.TryEnqueueAsync(expected)).Should().BeTrue();

        (await pending).Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Queue_SupportsConcurrentProducersForSingleConsumer()
    {
        const int producerCount = 8;
        const int itemsPerProducer = 100;
        const int totalItems = producerCount * itemsPerProducer;
        var queue = CreateQueue(totalItems);
        var accepted = new ConcurrentBag<bool>();

        async Task EnqueueRangeAsync(int producer)
        {
            for (var index = 0; index < itemsPerProducer; index++)
            {
                var id = $"webhook-{producer}-{index}";
                accepted.Add(await queue.TryEnqueueAsync(new WebhookWorkItem(id, "payments")));
                await Task.Yield();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, producerCount).Select(EnqueueRangeAsync));

        accepted.Count(acceptedItem => acceptedItem).Should().Be(totalItems);
        var dequeuedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < totalItems; index++)
        {
            dequeuedIds.Add((await queue.DequeueAsync()).WebhookId);
        }

        dequeuedIds.Should().BeEquivalentTo(Enumerable.Range(0, producerCount)
            .SelectMany(producer => Enumerable.Range(0, itemsPerProducer)
                .Select(index => $"webhook-{producer}-{index}")));
    }

    [Fact]
    public async Task TryEnqueueAsync_AcceptsExactlyCapacityAndFreesOneSlotAfterDequeue()
    {
        var queue = CreateQueue(capacity: 2);
        var first = new WebhookWorkItem("webhook-1", "payments");
        var second = new WebhookWorkItem("webhook-2", "payments");
        var third = new WebhookWorkItem("webhook-3", "payments");

        (await queue.TryEnqueueAsync(first)).Should().BeTrue();
        (await queue.TryEnqueueAsync(second)).Should().BeTrue();
        (await queue.TryEnqueueAsync(third)).Should().BeFalse();

        (await queue.DequeueAsync()).Should().BeSameAs(first);
        (await queue.TryEnqueueAsync(third)).Should().BeTrue();
    }

    [Fact]
    public async Task TryEnqueueAsync_WithPreCancelledToken_ThrowsWithoutAcceptingItem()
    {
        var queue = CreateQueue(capacity: 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelledItem = new WebhookWorkItem("webhook-cancelled", "payments");
        var acceptedItem = new WebhookWorkItem("webhook-accepted", "payments");
        var overflowItem = new WebhookWorkItem("webhook-overflow", "payments");

        Func<Task> act = async () => { _ = await queue.TryEnqueueAsync(cancelledItem, cancellation.Token); };

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await queue.TryEnqueueAsync(acceptedItem)).Should().BeTrue();
        (await queue.TryEnqueueAsync(overflowItem)).Should().BeFalse();
    }

    [Fact]
    public async Task DequeueAsync_WithPreCancelledToken_ThrowsWithoutConsumingBufferedItem()
    {
        var queue = CreateQueue(capacity: 1);
        var expected = new WebhookWorkItem("webhook-buffered", "payments");
        (await queue.TryEnqueueAsync(expected)).Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> act = async () => { _ = await queue.DequeueAsync(cancellation.Token); };

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await queue.DequeueAsync()).Should().BeSameAs(expected);
    }

    [Fact]
    public async Task DequeueAsync_WhenWaitingAndCancelled_ThrowsOperationCanceledException()
    {
        var queue = CreateQueue(capacity: 1);
        using var cancellation = new CancellationTokenSource();

        var pending = queue.DequeueAsync(cancellation.Token).AsTask();
        pending.IsCompleted.Should().BeFalse();
        await cancellation.CancelAsync();

        Func<Task> act = async () => { _ = await pending; };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CompleteAsync_WithPreCancelledToken_ThrowsWithoutCompletingWriter()
    {
        var queue = CreateQueue(capacity: 1);
        var expected = new WebhookWorkItem("webhook-buffered", "payments");
        (await queue.TryEnqueueAsync(expected)).Should().BeTrue();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> act = async () => { await queue.CompleteAsync(cancellation.Token); };

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await queue.DequeueAsync()).Should().BeSameAs(expected);
        await queue.CompleteAsync();
        Func<Task> readAfterCompletion = async () => { _ = await queue.DequeueAsync(); };
        await readAfterCompletion.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public async Task CompleteAsync_AllowsBufferedItemsToDrainBeforeSignallingCompletion()
    {
        var queue = CreateQueue(capacity: 2);
        var first = new WebhookWorkItem("webhook-1", "payments");
        var second = new WebhookWorkItem("webhook-2", "payments");
        (await queue.TryEnqueueAsync(first)).Should().BeTrue();
        (await queue.TryEnqueueAsync(second)).Should().BeTrue();

        await queue.CompleteAsync();

        (await queue.DequeueAsync()).Should().BeSameAs(first);
        (await queue.DequeueAsync()).Should().BeSameAs(second);
        Func<Task> act = async () => { _ = await queue.DequeueAsync(); };
        await act.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public async Task TryEnqueueAsync_AfterCompletion_ReturnsFalse()
    {
        var queue = CreateQueue(capacity: 1);
        await queue.CompleteAsync();

        var accepted = await queue.TryEnqueueAsync(new WebhookWorkItem("webhook-late", "payments"));

        accepted.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAsync_CalledMoreThanOnce_IsIdempotent()
    {
        var queue = CreateQueue(capacity: 1);

        await queue.CompleteAsync();
        await queue.CompleteAsync();

        Func<Task> act = async () => { _ = await queue.DequeueAsync(); };
        await act.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public void WebhookWorkItem_ExposesOnlyImmutableReferenceProperties()
    {
        var item = new WebhookWorkItem("webhook-1", "payments");
        var properties = typeof(WebhookWorkItem).GetProperties();

        item.WebhookId.Should().Be("webhook-1");
        item.Provider.Should().Be("payments");
        properties.Select(property => property.Name).Should().Equal("WebhookId", "Provider");
        foreach (var property in properties)
        {
            var setter = property.SetMethod;
            setter.Should().NotBeNull();
            setter!.ReturnParameter.GetRequiredCustomModifiers()
                .Should().Contain(typeof(System.Runtime.CompilerServices.IsExternalInit));
        }
    }

    [Fact]
    public void QueueOptions_DefaultCapacityIsPositiveAndValidatorAcceptsIt()
    {
        var options = new WebhookKitOptions();

        options.Queue.Capacity.Should().Be(1024);
        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QueueOptions_ValidatorRejectsNonPositiveCapacity(int capacity)
    {
        var options = new WebhookKitOptions
        {
            Queue = new WebhookQueueOptions { Capacity = capacity }
        };

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void QueueOptions_ValidatorRejectsMissingConfiguration()
    {
        var options = new WebhookKitOptions
        {
            Queue = null!
        };

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void AddWebhookKit_RegistersConcreteAndInterfaceQueueAsSameSingleton()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit();
        using var provider = services.BuildServiceProvider();

        var interfaceQueue = provider.GetRequiredService<IWebhookQueue>();
        var concreteQueue = provider.GetRequiredService<ChannelWebhookQueue>();

        interfaceQueue.Should().BeSameAs(concreteQueue);
        provider.GetRequiredService<ChannelWebhookQueue>().Should().BeSameAs(concreteQueue);
        services.Should().ContainSingle(service =>
            service.ServiceType == typeof(IWebhookQueue) && service.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddWebhookKit_PreservesCustomQueueRegisteredBeforeDefaults()
    {
        var services = new ServiceCollection();
        var customQueue = new StubWebhookQueue();
        services.AddSingleton<IWebhookQueue>(customQueue);
        services.AddWebhookKit();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookQueue>().Should().BeSameAs(customQueue);
    }

    [Fact]
    public async Task AddWebhookKit_ConfiguresQueueCapacity()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit(options => options.Queue.Capacity = 1);
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IWebhookQueue>();

        (await queue.TryEnqueueAsync(new WebhookWorkItem("webhook-1", "payments"))).Should().BeTrue();
        (await queue.TryEnqueueAsync(new WebhookWorkItem("webhook-2", "payments"))).Should().BeFalse();
    }

    private static ChannelWebhookQueue CreateQueue(int capacity)
    {
        var options = new WebhookKitOptions
        {
            Queue = new WebhookQueueOptions { Capacity = capacity }
        };
        return new ChannelWebhookQueue(Microsoft.Extensions.Options.Options.Create(options));
    }

    private sealed class StubWebhookQueue : IWebhookQueue
    {
        public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
