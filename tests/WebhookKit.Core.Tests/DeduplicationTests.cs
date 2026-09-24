using System.Text;
using FluentAssertions;
using WebhookKit.Abstractions;
using WebhookKit.Core.Deduplication;
using WebhookKit.Core.Options;
using WebhookKit.Core.Stores;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class DeduplicationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithEventId_NormalizesProviderAndPreservesOpaqueEventId()
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions();

        var first = factory.Create("  Stripe  ", " EvT:Case  ", Encoding.UTF8.GetBytes("{}"), options);
        var second = factory.Create("stripe", " EvT:Case  ", Encoding.UTF8.GetBytes("different"), options);

        first.Should().Be("stripe: EvT:Case  ");
        second.Should().Be("stripe: EvT:Case  ");
    }

    [Fact]
    public void Create_WithFallbackEnabled_UsesProviderSeparatorAndExactBodyBytes()
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions { AllowBodyHashFallback = true };
        byte[] body = [0x7B, 0x00, 0xFF, 0x0A];

        var first = factory.Create(" STRIPE ", null, body, options);
        var second = factory.Create("stripe", null, body, options);

        first.Should().Be("stripe:sha256:c18ca720851a5fe83045804b876d3ad0270a2ecf86a2f4bb7f08cfd697af4245");
        second.Should().Be(first);
    }

    [Fact]
    public void Create_WithFallbackEnabled_ChangesKeyWhenBodyChanges()
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions { AllowBodyHashFallback = true };

        var first = factory.Create("stripe", null, Encoding.UTF8.GetBytes("{\"id\":1}"), options);
        var second = factory.Create("stripe", null, Encoding.UTF8.GetBytes("{\"id\":2}"), options);

        first.Should().NotBe(second);
        first.Should().StartWith("stripe:sha256:");
        second.Should().StartWith("stripe:sha256:");
    }

    [Fact]
    public void Create_WithoutEventId_RejectsByDefaultWithSafeConfigurationException()
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions();

        var act = () => factory.Create("stripe", null, Encoding.UTF8.GetBytes("{}"), options);

        act.Should().Throw<WebhookConfigurationException>()
            .WithMessage("*Event ID*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutEventId_RejectsEveryMissingFormByDefault(string? eventId)
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions();

        var act = () => factory.Create("stripe", eventId, Encoding.UTF8.GetBytes("{}"), options);

        act.Should().Throw<WebhookConfigurationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithFallbackEnabled_TreatsMissingEventIdFormsAsAbsent(string eventId)
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions { AllowBodyHashFallback = true };

        var key = factory.Create("stripe", eventId, Encoding.UTF8.GetBytes("{}"), options);

        key.Should().StartWith("stripe:sha256:");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidProvider_RejectsProvider(string? provider)
    {
        var factory = new WebhookDeduplicationKeyFactory();
        var options = new WebhookProviderOptions();

        var act = () => factory.Create(provider!, "evt_1", Encoding.UTF8.GetBytes("{}"), options);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task TryAcquireAsync_ConcurrentDuplicate_AllowsExactlyOneClaim()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        var deduplicator = new DefaultWebhookDeduplicator(store);
        var tasks = Enumerable.Range(0, 100)
            .Select(index => Task.Run(() => deduplicator.TryAcquireAsync(CreateRecord($"webhook-{index}"), CancellationToken.None).AsTask()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(result => result).Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAsync_DelegatesTheRecordToTheAtomicStoreOperation()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        var deduplicator = new DefaultWebhookDeduplicator(store);
        var record = CreateRecord("webhook-1");

        (await deduplicator.TryAcquireAsync(record)).Should().BeTrue();
        (await store.GetAsync("stripe", "evt_1")).Should().NotBeNull();
        (await deduplicator.TryAcquireAsync(record)).Should().BeFalse();
    }

    private static WebhookRecord CreateRecord(string id)
    {
        return new WebhookRecord
        {
            Id = id,
            Provider = "stripe",
            EventId = "evt_1",
            DeduplicationKey = "stripe:evt_1",
            HttpMethod = "POST",
            RequestPath = "/webhook",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            RawBody = [1, 2, 3],
            ReceivedAt = Start
        };
    }
}
