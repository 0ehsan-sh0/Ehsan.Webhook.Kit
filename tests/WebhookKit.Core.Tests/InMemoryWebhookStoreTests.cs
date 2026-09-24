using FluentAssertions;
using WebhookKit.Abstractions;
using WebhookKit.Core.Stores;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class InMemoryWebhookStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryCreateAsync_ConcurrentDuplicateKey_AllowsExactlyOneRecord()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        var tasks = Enumerable.Range(0, 100)
            .Select(index => Task.Run(() => store.TryCreateAsync(CreateRecord($"webhook-{index}"), CancellationToken.None).AsTask()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(result => result).Should().Be(1);
        (await store.GetAsync("stripe", "evt_1")).Should().NotBeNull();
        (await store.GetRecoverableAsync(Start, TimeSpan.Zero, 200, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task LookupMethods_ReturnIndependentCopiesOfMutableState()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        var sourceHeaderValues = new List<string> { "original" };
        var original = CreateRecord(
            "webhook-1",
            rawBody: [1, 2, 3],
            headers: new Dictionary<string, IReadOnlyList<string>> { ["X-Test"] = sourceHeaderValues });

        (await store.TryCreateAsync(original)).Should().BeTrue();
        original.RawBody![0] = 9;
        sourceHeaderValues.Add("changed");

        var first = await store.GetByWebhookIdAsync("webhook-1");
        first.Should().NotBeNull();
        first!.RawBody.Should().Equal(1, 2, 3);
        first.Headers["X-Test"].Should().Equal("original");
        first.RawBody![0] = 8;
        var firstHeaderValues = (IList<string>)first.Headers["X-Test"];
        var mutateFirstHeader = () => firstHeaderValues.Add("changed-again");
        mutateFirstHeader.Should().Throw<NotSupportedException>();

        var second = await store.GetByWebhookIdAsync("webhook-1");
        second!.RawBody.Should().Equal(1, 2, 3);
        second.Headers["X-Test"].Should().Equal("original");
    }

    [Fact]
    public async Task UpdateAsync_PersistsMutableStatusChanges()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status = WebhookProcessingStatus.Ignored;
        record.FailureReason = "no-handler";
        await store.UpdateAsync(record);

        var stored = await store.GetByWebhookIdAsync("webhook-1");
        stored!.Status.Should().Be(WebhookProcessingStatus.Ignored);
        stored.FailureReason.Should().Be("no-handler");
    }

    [Fact]
    public async Task TryClaimAsync_TransitionsReceivedRecordAndSetsLeaseMetadata()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();

        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(5))).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status.Should().Be(WebhookProcessingStatus.Processing);
        record.ProcessingLeaseOwner.Should().Be("worker-1");
        record.ProcessingLeaseExpiresAt.Should().Be(Start.AddMinutes(5));
        record.AttemptCount.Should().Be(1);
        record.LastAttemptAt.Should().Be(Start);
    }

    [Fact]
    public async Task TryClaimAsync_RejectsDifferentOwnerWhileLeaseIsActive()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(5))).Should().BeTrue();

        (await store.TryClaimAsync("webhook-1", "worker-2", TimeSpan.FromMinutes(5))).Should().BeFalse();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.ProcessingLeaseOwner.Should().Be("worker-1");
        record.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task TryClaimAsync_ReclaimsExpiredProcessingLeaseAtomically()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));

        (await store.TryClaimAsync("webhook-1", "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status.Should().Be(WebhookProcessingStatus.Processing);
        record.ProcessingLeaseOwner.Should().Be("worker-2");
        record.ProcessingLeaseExpiresAt.Should().Be(Start.AddMinutes(3));
        record.AttemptCount.Should().Be(2);
        record.LastAttemptAt.Should().Be(Start.AddMinutes(2));
    }

    [Fact]
    public async Task ReleaseAsync_AllowsCurrentOwnerAndRejectsStaleOwner()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.ReleaseAsync("webhook-1", "worker-2")).Should().BeFalse();
        (await store.ReleaseAsync("webhook-1", "worker-1")).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
        record.ProcessingLeaseOwner.Should().BeNull();
        record.ProcessingLeaseExpiresAt.Should().BeNull();
        (await store.TryClaimAsync("webhook-1", "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_StaleLeaseOwner_DoesNotOverwriteNewOwner()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        var stale = await store.GetByWebhookIdAsync("webhook-1");
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.TryClaimAsync("webhook-1", "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();

        stale!.Status = WebhookProcessingStatus.Processed;
        stale.ProcessedAt = Start.AddSeconds(30);
        var act = async () => await store.UpdateAsync(stale);

        await act.Should().NotThrowAsync();
        var current = await store.GetByWebhookIdAsync("webhook-1");
        current!.Status.Should().Be(WebhookProcessingStatus.Processing);
        current.ProcessingLeaseOwner.Should().Be("worker-2");
    }

    [Fact]
    public async Task MarkProcessedAsync_RequiresCurrentOwnerAndClearsLease()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkProcessedAsync("webhook-1", "worker-2", Start.AddSeconds(10))).Should().BeFalse();
        (await store.MarkProcessedAsync("webhook-1", "worker-1", Start.AddSeconds(10))).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.ProcessedAt.Should().Be(Start.AddSeconds(10));
        record.ProcessingLeaseOwner.Should().BeNull();
        record.ProcessingLeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_StoresSafeDiagnosticsAndRequiresCurrentOwner()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkFailedAsync("webhook-1", "worker-2", Start.AddSeconds(10), "wrong-owner")).Should().BeFalse();
        (await store.MarkFailedAsync("webhook-1", "worker-1", Start.AddSeconds(10), "handler-failed")).Should().BeTrue();

        var record = await store.GetByWebhookIdAsync("webhook-1");
        record!.Status.Should().Be(WebhookProcessingStatus.Failed);
        record.FailedAt.Should().Be(Start.AddSeconds(10));
        record.FailureReason.Should().Be("handler-failed");
        record.FailureCode.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetRecoverableAsync_ReturnsReceivedAndExpiredProcessingRecordsOnly()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord("received"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("expired", "evt_expired"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("active", "evt_active"))).Should().BeTrue();
        (await store.TryClaimAsync("expired", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.TryClaimAsync("active", "worker-1", TimeSpan.FromHours(1))).Should().BeTrue();
        await store.MarkProcessedAsync("active", "worker-1", Start.AddSeconds(5));

        var recoverable = await store.GetRecoverableAsync(Start.AddMinutes(2), TimeSpan.FromMinutes(1), 20);

        recoverable.Select(record => record.Id).Should().BeEquivalentTo(["received", "expired"]);
    }

    [Fact]
    public async Task GetRecoverableAsync_UsesExpiredLeaseAgeBeforeReturningProcessingRecord()
    {
        var clock = new FakeWebhookClock(Start);
        var store = new InMemoryWebhookStore(clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        var beforeGrace = await store.GetRecoverableAsync(Start.AddMinutes(2).AddSeconds(-1), TimeSpan.FromMinutes(1), 20);
        var afterGrace = await store.GetRecoverableAsync(Start.AddMinutes(2), TimeSpan.FromMinutes(1), 20);

        beforeGrace.Should().BeEmpty();
        afterGrace.Should().ContainSingle();
    }

    [Fact]
    public async Task TryCreateAsync_AllowsNullEventIdOnlyForProviderScopedSha256Fallback()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        var fallback = CreateRecord("fallback", eventId: null);
        var invalid = CreateRecord("invalid", eventId: null, deduplicationKey: "stripe:evt_missing");

        (await store.TryCreateAsync(fallback)).Should().BeTrue();
        var act = async () => await store.TryCreateAsync(invalid);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StoreMethods_ValidateRequiredInputs()
    {
        var store = new InMemoryWebhookStore(new FakeWebhookClock(Start));
        var recordWithEmptyOwner = CreateRecord();
        recordWithEmptyOwner.ProcessingLeaseOwner = " ";

        var createWithoutProvider = async () => await store.TryCreateAsync(new WebhookRecord
        {
            Id = "webhook-1",
            Provider = " ",
            EventId = "evt_1",
            DeduplicationKey = "stripe:evt_1",
            HttpMethod = "POST",
            RequestPath = "/webhook",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            ReceivedAt = Start
        });
        var getWithoutProvider = async () => await store.GetAsync(" ", "evt_1");
        var createWithEmptyOwner = async () => await store.TryCreateAsync(recordWithEmptyOwner);
        var claimWithoutOwner = async () => await store.TryClaimAsync("webhook-1", " ", TimeSpan.FromMinutes(1));
        var claimWithInvalidDuration = async () => await store.TryClaimAsync("webhook-1", "worker", TimeSpan.Zero);

        await createWithoutProvider.Should().ThrowAsync<ArgumentException>();
        await getWithoutProvider.Should().ThrowAsync<ArgumentException>();
        await createWithEmptyOwner.Should().ThrowAsync<ArgumentException>();
        await claimWithoutOwner.Should().ThrowAsync<ArgumentException>();
        await claimWithInvalidDuration.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static WebhookRecord CreateRecord(
        string id = "webhook-1",
        string provider = "stripe",
        string? eventId = "evt_1",
        byte[]? rawBody = null,
        string? deduplicationKey = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null)
    {
        var key = deduplicationKey ?? (eventId is null ? $"{provider}:sha256:{new string('a', 64)}" : $"{provider}:{eventId}");
        return new WebhookRecord
        {
            Id = id,
            Provider = provider,
            EventId = eventId,
            DeduplicationKey = key,
            HttpMethod = "POST",
            RequestPath = "/webhook",
            Headers = headers ?? new Dictionary<string, IReadOnlyList<string>> { ["X-Test"] = new[] { "original" } },
            RawBody = rawBody ?? [1, 2, 3],
            ReceivedAt = Start,
            Status = WebhookProcessingStatus.Received
        };
    }
}
