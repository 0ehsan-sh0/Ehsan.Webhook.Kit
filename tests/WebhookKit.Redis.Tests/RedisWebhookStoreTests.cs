using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using WebhookKit.Abstractions;
using WebhookKit.Redis;
using WebhookKit.Testing;
using Xunit;

#pragma warning disable CA1707

namespace WebhookKit.Redis.Tests;

public sealed class RedisWebhookStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private const string DefaultWebhookId = "01J00000000000000000000000";
    private const string DefaultDeduplicationHash = "3d804092dc2d75fe99f010fbea0f2a2f09fd31cbbce76334a890b4664fa6271f";

    [Fact]
    public async Task TryCreateAsync_OneHundredConcurrentDuplicates_CreatesExactlyOneRecordAndMarker()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        var tasks = Enumerable.Range(0, 100)
            .Select(index => Task.Run(async () => await store.TryCreateAsync(CreateRecord(Id(index)), CancellationToken.None)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(result => result).Should().Be(1);
        database.Keys.Count(key => key.StartsWith("test:record:", StringComparison.Ordinal)).Should().Be(1);
        database.Keys.Count(key => key.StartsWith("test:dedup:", StringComparison.Ordinal)).Should().Be(1);
        database.GetIndexMembers().Should().ContainSingle();
        var stored = await store.GetAsync("stripe", "evt_1");
        stored.Should().NotBeNull();
        (await store.GetByWebhookIdAsync(stored!.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task TryCreateAsync_ExistingCorruptDeduplicationState_CreatesNoRecord()
    {
        var database = new StatefulRedisDatabase();
        var deduplicationKey = $"test:dedup:{DefaultDeduplicationHash}";
        database.SeedKey(deduplicationKey, "corrupt-marker");
        var store = CreateStore(database);

        var created = await store.TryCreateAsync(CreateRecord());

        created.Should().BeFalse();
        database.GetRawValue(deduplicationKey).Should().Be("corrupt-marker");
        database.Keys.Should().NotContain(key => key.StartsWith("test:record:", StringComparison.Ordinal));
        database.GetIndexMembers().Should().BeEmpty();
    }

    [Fact]
    public async Task TryCreateAsync_ExistingRecordWithoutDeduplicationMarker_CreatesNoMarker()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        var create = database.Commands.Single(command => command.Operation == RedisWebhookOperation.Create);
        database.RemoveKey(create.Keys[0]);
        database.ClearCommands();

        var created = await store.TryCreateAsync(CreateRecord(eventId: "evt_second"));

        created.Should().BeFalse();
        database.GetRawValue($"test:dedup:{HashForStripeEvent("evt_second")}").Should().BeNull();
        (await store.GetByWebhookIdAsync(DefaultWebhookId))!.EventId.Should().Be("evt_1");
        (await store.GetAsync("stripe", "evt_second")).Should().BeNull();
    }

    [Fact]
    public async Task TryCreateAsync_PreexistingRecoverableIndexMember_CreatesNoDedupMarkerOrRecord()
    {
        var database = new StatefulRedisDatabase();
        database.SeedIndexMember(DefaultWebhookId);
        var store = CreateStore(database);

        var created = await store.TryCreateAsync(CreateRecord());

        created.Should().BeFalse();
        database.GetRawValue($"test:dedup:{DefaultDeduplicationHash}").Should().BeNull();
        database.GetRawValue($"test:record:{DefaultWebhookId}").Should().BeNull();
        database.GetIndexMembers().Should().ContainSingle().Which.Should().Be(DefaultWebhookId);
    }

    [Fact]
    public async Task Store_RoundTripsEveryWebhookRecordFieldWithoutSharedMutableState()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        var record = CreateCompleteRecord();

        (await store.TryCreateAsync(record)).Should().BeTrue();
        var first = await store.GetByWebhookIdAsync(record.Id);

        first.Should().NotBeNull();
        first!.Id.Should().Be(record.Id);
        first.CorrelationId.Should().Be(record.CorrelationId);
        first.Provider.Should().Be(record.Provider);
        first.EventId.Should().Be(record.EventId);
        first.DeduplicationKey.Should().Be(record.DeduplicationKey);
        first.EventType.Should().Be(record.EventType);
        first.HttpMethod.Should().Be(record.HttpMethod);
        first.RequestPath.Should().Be(record.RequestPath);
        first.Headers["X-Signature"].Should().Equal("signature-one", "signature-two");
        first.ContentType.Should().Be(record.ContentType);
        first.ContentLength.Should().Be(record.ContentLength);
        first.RawBody.Should().Equal(0, 1, 2, 254, 255);
        first.ReceivedAt.Should().Be(record.ReceivedAt);
        first.ProviderTimestamp.Should().Be(record.ProviderTimestamp);
        first.Status.Should().Be(record.Status);
        first.AttemptCount.Should().Be(record.AttemptCount);
        first.LastAttemptAt.Should().Be(record.LastAttemptAt);
        first.ProcessingLeaseOwner.Should().Be(record.ProcessingLeaseOwner);
        first.ProcessingLeaseExpiresAt.Should().Be(record.ProcessingLeaseExpiresAt);
        first.ProcessedAt.Should().Be(record.ProcessedAt);
        first.FailedAt.Should().Be(record.FailedAt);
        first.FailureReason.Should().Be(record.FailureReason);
        first.FailureCode.Should().Be(record.FailureCode);
        first.RawBody![0] = 99;
        var firstHeaderValues = (IList<string>)first.Headers["X-Signature"];
        var mutateFirstHeader = () => firstHeaderValues[0] = "changed";
        mutateFirstHeader.Should().Throw<NotSupportedException>();
        var second = await store.GetByWebhookIdAsync(record.Id);
        second!.RawBody.Should().Equal(0, 1, 2, 254, 255);
        second.Headers["X-Signature"].Should().Equal("signature-one", "signature-two");
    }

    [Fact]
    public async Task Serialization_ContainsOnlyDedicatedWebhookRecordDtoFields()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, new RedisWebhookStoreOptions
        {
            KeyPrefix = "serialization-test",
            DeduplicationRetention = TimeSpan.FromHours(1),
            RecordRetention = TimeSpan.FromHours(2)
        });

        (await store.TryCreateAsync(CreateCompleteRecord())).Should().BeTrue();

        var payload = database.Commands.Single(command => command.Operation == RedisWebhookOperation.Create).Values[1];
        using var json = JsonDocument.Parse(payload);
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
        [
            "id",
            "correlationId",
            "provider",
            "eventId",
            "deduplicationKey",
            "eventType",
            "httpMethod",
            "requestPath",
            "headers",
            "contentType",
            "contentLength",
            "rawBody",
            "receivedAt",
            "providerTimestamp",
            "status",
            "attemptCount",
            "lastAttemptAt",
            "processingLeaseOwner",
            "processingLeaseExpiresAt",
            "processedAt",
            "failedAt",
            "failureReason",
            "failureCode"
        ]);
        payload.Should().Contain("AAEC/v8=");
        payload.Should().NotContain("serialization-test");
        payload.Should().NotContain("connectionString");
        payload.Should().NotContain("exception");
        payload.Should().NotContain("secret");
    }

    [Fact]
    public async Task Create_UsesSevenDayDeduplicationAndThirtyDayRecordRetentionByDefault()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);

        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();

        var create = database.Commands.Single(command => command.Operation == RedisWebhookOperation.Create);
        database.GetTimeToLive(create.Keys[0]).Should().Be(TimeSpan.FromDays(7));
        database.GetTimeToLive(create.Keys[1]).Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task StateTransitions_PreserveRemainingRecordTtlAndIndependentDeduplicationTtl()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, new RedisWebhookStoreOptions
        {
            DeduplicationRetention = TimeSpan.FromSeconds(40),
            RecordRetention = TimeSpan.FromSeconds(50)
        });
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        var create = database.Commands.Single(command => command.Operation == RedisWebhookOperation.Create);
        database.Advance(TimeSpan.FromSeconds(5));

        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        database.GetTimeToLive(create.Keys[0]).Should().Be(TimeSpan.FromSeconds(35));
        database.GetTimeToLive(create.Keys[1]).Should().Be(TimeSpan.FromSeconds(45));
        var claim = database.Commands.Single(command => command.Operation == RedisWebhookOperation.Claim);
        claim.Script.Should().Contain("KEEPTTL");
        claim.Script.Should().NotContain("PEXPIRE");
    }

    [Fact]
    public void Options_RejectUnsafePrefixesAndNonPositiveTtls()
    {
        var invalidPrefix = () => CreateStore(new StatefulRedisDatabase(), new RedisWebhookStoreOptions { KeyPrefix = "tenant:{other}" });
        var zeroDeduplication = () => CreateStore(new StatefulRedisDatabase(), new RedisWebhookStoreOptions { DeduplicationRetention = TimeSpan.Zero });
        var subMillisecondRecord = () => CreateStore(new StatefulRedisDatabase(), new RedisWebhookStoreOptions { RecordRetention = TimeSpan.FromTicks(1) });

        invalidPrefix.Should().Throw<ArgumentException>();
        zeroDeduplication.Should().Throw<ArgumentException>();
        subMillisecondRecord.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task LookupMethods_AreProviderScopedAndWebhookIdAddressable()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        var stripe = CreateRecord("01J00000000000000000000001", "stripe", "evt_same");
        var github = CreateRecord("01J00000000000000000000002", "github", "evt_same");

        (await store.TryCreateAsync(stripe)).Should().BeTrue();
        (await store.TryCreateAsync(github)).Should().BeTrue();

        (await store.GetAsync("STRIPE", "evt_same"))!.Id.Should().Be(stripe.Id);
        (await store.GetAsync("GitHub", "evt_same"))!.Id.Should().Be(github.Id);
        (await store.GetByWebhookIdAsync(github.Id))!.EventId.Should().Be("evt_same");
    }

    [Fact]
    public async Task Keys_HashProviderScopedDeduplicationIdentityAndValidateWebhookId()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        var eventId = "evt:{secret}:raw-body-hash";
        var bodyHash = new string('a', 64);

        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000003", eventId: eventId))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord(
            "01J00000000000000000000004",
            eventId: null,
            deduplicationKey: $"stripe:sha256:{bodyHash}"))).Should().BeTrue();

        var commands = database.Commands.Where(command => command.Operation == RedisWebhookOperation.Create).ToArray();
        commands.Should().HaveCount(2);
        commands.SelectMany(command => command.Keys).Should().OnlyContain(key =>
            key.StartsWith("test:dedup:", StringComparison.Ordinal) ||
            key.StartsWith("test:record:", StringComparison.Ordinal) ||
            key == "test:recoverable");
        commands.SelectMany(command => command.Keys).Should().NotContain(key =>
            key.Contains(eventId, StringComparison.Ordinal) ||
            key.Contains("stripe", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(bodyHash, StringComparison.Ordinal));
        commands[0].Keys[0].Should().Be($"test:dedup:{HashForStripeEvent(eventId)}");

        var invalid = CreateRecord("evt:{key-injection}");
        var act = async () => await store.TryCreateAsync(invalid);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeduplicationHash_DoesNotCollideWhenDelimiterMovesAcrossProviderAndEventBoundary()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        var first = CreateRecord("01J00000000000000000000005", "stripe\0internal", "evt");
        var second = CreateRecord("01J00000000000000000000006", "stripe", "internal\0evt");

        (await store.TryCreateAsync(first)).Should().BeTrue();
        (await store.TryCreateAsync(second)).Should().BeTrue();

        var deduplicationKeys = database.Commands
            .Where(command => command.Operation == RedisWebhookOperation.Create)
            .Select(command => command.Keys[0])
            .ToArray();
        deduplicationKeys.Should().HaveCount(2);
        deduplicationKeys.Distinct(StringComparer.Ordinal).Should().HaveCount(2);
    }

    [Fact]
    public async Task TryClaimAsync_SetsLeaseAndAttemptMetadata_AndRejectsConflictingOwner()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();

        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(5))).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(5))).Should().BeFalse();

        var record = await store.GetByWebhookIdAsync(DefaultWebhookId);
        record!.Status.Should().Be(WebhookProcessingStatus.Processing);
        record.ProcessingLeaseOwner.Should().Be("worker-1");
        record.ProcessingLeaseExpiresAt.Should().Be(Start.AddMinutes(5));
        record.AttemptCount.Should().Be(1);
        record.LastAttemptAt.Should().Be(Start);
    }

    [Fact]
    public async Task TryClaimAsync_ReclaimsExpiredLease_AndStaleOwnerCannotOverwriteNewOwner()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));

        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();

        var reclaimed = await store.GetByWebhookIdAsync(DefaultWebhookId);
        reclaimed!.Status.Should().Be(WebhookProcessingStatus.Processing);
        reclaimed.ProcessingLeaseOwner.Should().Be("worker-2");
        reclaimed.ProcessingLeaseExpiresAt.Should().Be(Start.AddMinutes(3));
        reclaimed.AttemptCount.Should().Be(2);
        reclaimed.LastAttemptAt.Should().Be(Start.AddMinutes(2));
        (await store.ReleaseAsync(DefaultWebhookId, "worker-1")).Should().BeFalse();
        (await store.MarkProcessedAsync(DefaultWebhookId, "worker-1", Start.AddMinutes(2))).Should().BeFalse();
        (await store.MarkFailedAsync(DefaultWebhookId, "worker-1", Start.AddMinutes(2), "stale-failure")).Should().BeFalse();
        (await store.GetByWebhookIdAsync(DefaultWebhookId))!.ProcessingLeaseOwner.Should().Be("worker-2");
    }

    [Fact]
    public async Task ReleaseAsync_RequiresCurrentOwnerAndMakesRecordImmediatelyRecoverable()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.ReleaseAsync(DefaultWebhookId, "worker-2")).Should().BeFalse();
        (await store.ReleaseAsync(DefaultWebhookId, "worker-1")).Should().BeTrue();

        var released = await store.GetByWebhookIdAsync(DefaultWebhookId);
        released!.Status.Should().Be(WebhookProcessingStatus.Received);
        released.ProcessingLeaseOwner.Should().BeNull();
        released.ProcessingLeaseExpiresAt.Should().BeNull();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();
        var recovered = await store.GetRecoverableAsync(Start, TimeSpan.Zero, 10);
        recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkProcessedAsync_RequiresCurrentOwner_ClearsLeaseAndIsTerminal()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkProcessedAsync(DefaultWebhookId, "worker-2", Start.AddSeconds(10))).Should().BeFalse();
        (await store.MarkProcessedAsync(DefaultWebhookId, "worker-1", Start.AddSeconds(10))).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(1))).Should().BeFalse();

        var processed = await store.GetByWebhookIdAsync(DefaultWebhookId);
        processed!.Status.Should().Be(WebhookProcessingStatus.Processed);
        processed.ProcessedAt.Should().Be(Start.AddSeconds(10));
        processed.ProcessingLeaseOwner.Should().BeNull();
        processed.ProcessingLeaseExpiresAt.Should().BeNull();
        database.GetIndexMembers().Should().BeEmpty();
    }

    [Fact]
    public async Task MarkFailedAsync_RequiresCurrentOwner_StoresOnlySafeDiagnosticsAndIsTerminal()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkFailedAsync(DefaultWebhookId, "worker-2", Start.AddSeconds(10), "wrong-owner")).Should().BeFalse();
        (await store.MarkFailedAsync(DefaultWebhookId, "worker-1", Start.AddSeconds(10), "handler failed with secret details")).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));

        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(1))).Should().BeFalse();
        var failed = await store.GetByWebhookIdAsync(DefaultWebhookId);
        failed!.Status.Should().Be(WebhookProcessingStatus.Failed);
        failed.FailedAt.Should().Be(Start.AddSeconds(10));
        failed.FailureReason.Should().Be("processing-failed");
        failed.FailureCode.Should().Be("processing-failed");
        failed.ProcessingLeaseOwner.Should().BeNull();
        database.GetIndexMembers().Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecoverableAsync_ReturnsReceivedAndGraceExpiredProcessing_AndCleansIndex()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000010", eventId: "evt_received"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000011", eventId: "evt_expired"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000012", eventId: "evt_active"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000013", eventId: "evt_processed"))).Should().BeTrue();
        (await store.TryClaimAsync("01J00000000000000000000011", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.TryClaimAsync("01J00000000000000000000012", "worker-1", TimeSpan.FromHours(1))).Should().BeTrue();
        (await store.MarkProcessedAsync("01J00000000000000000000013", "worker-none", Start)).Should().BeFalse();
        database.SeedRawRecord("01J00000000000000000000013", CreateSerializedTerminalRecord("01J00000000000000000000013"));
        database.SeedIndexMember("01J00000000000000000000013");
        database.SeedIndexMember("01J00000000000000000000999");
        database.ClearCommands();

        var recoverable = await store.GetRecoverableAsync(Start.AddMinutes(2), TimeSpan.FromMinutes(1), 20);

        recoverable.Select(record => record.Id).Should().BeEquivalentTo(
        [
            "01J00000000000000000000010",
            "01J00000000000000000000011"
        ]);
        database.GetIndexMembers().Should().BeEquivalentTo(
        [
            "01J00000000000000000000010",
            "01J00000000000000000000011",
            "01J00000000000000000000012"
        ]);
        database.SortedSetReads.Should().ContainSingle();
        database.SortedSetReads[0].Limit.Should().Be(20);
    }

    [Fact]
    public async Task UpdateAsync_CurrentOwnerCanUpdate_StaleOwnerCannotOverwriteNewerState()
    {
        var clock = new FakeWebhookClock(Start);
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database, clock: clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        var stale = await store.GetByWebhookIdAsync(DefaultWebhookId);
        stale!.FailureReason = "stale-failure";
        stale.Status = WebhookProcessingStatus.Processed;
        stale.ProcessedAt = Start.AddSeconds(30);
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.TryClaimAsync(DefaultWebhookId, "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();
        var current = await store.GetByWebhookIdAsync(DefaultWebhookId);
        current!.FailureReason = "retrying";

        await store.UpdateAsync(stale);
        await store.UpdateAsync(current);

        var stored = await store.GetByWebhookIdAsync(DefaultWebhookId);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.ProcessingLeaseOwner.Should().Be("worker-2");
        stored.FailureReason.Should().Be("retrying");
        stored.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task StoreMethods_CheckCancellationBeforeAndAfterRedisCommands()
    {
        var beforeDatabase = new StatefulRedisDatabase();
        var beforeStore = CreateStore(beforeDatabase);
        using var beforeCancellation = new CancellationTokenSource();
        beforeCancellation.Cancel();

        var beforeAct = async () => await beforeStore.GetByWebhookIdAsync(DefaultWebhookId, beforeCancellation.Token);
        await beforeAct.Should().ThrowAsync<OperationCanceledException>();
        beforeDatabase.Commands.Should().BeEmpty();

        var afterDatabase = new StatefulRedisDatabase();
        var afterStore = CreateStore(afterDatabase);
        using var afterCancellation = new CancellationTokenSource();
        afterDatabase.CancelAfterNextExecute(afterCancellation);

        var afterAct = async () => await afterStore.TryCreateAsync(CreateRecord(), afterCancellation.Token);
        await afterAct.Should().ThrowAsync<OperationCanceledException>();
        (await afterStore.GetByWebhookIdAsync(DefaultWebhookId)).Should().NotBeNull();
    }

    [Fact]
    public async Task GeneratedCommands_AreAtomicHashSafeAndPreserveTtl()
    {
        var database = new StatefulRedisDatabase();
        var store = CreateStore(database);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.ReleaseAsync(DefaultWebhookId, "worker-1")).Should().BeTrue();
        (await store.TryClaimAsync(DefaultWebhookId, "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.MarkProcessedAsync(DefaultWebhookId, "worker-1", Start)).Should().BeTrue();
        var processed = await store.GetByWebhookIdAsync(DefaultWebhookId);
        await store.UpdateAsync(processed!);
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000001", eventId: "evt_failed"))).Should().BeTrue();
        (await store.TryClaimAsync("01J00000000000000000000001", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.MarkFailedAsync("01J00000000000000000000001", "worker-1", Start, "handler-failed")).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("01J00000000000000000000002", eventId: "evt_recoverable"))).Should().BeTrue();
        (await store.GetRecoverableAsync(Start, TimeSpan.Zero, 5)).Should().ContainSingle();

        var scripts = database.Commands
            .GroupBy(command => command.Operation)
            .ToDictionary(group => group.Key, group => group.First().Script);
        scripts[RedisWebhookOperation.Create].Should().ContainAll("EXISTS", "PSETEX", "ZADD");
        scripts[RedisWebhookOperation.Claim].Should().ContainAll("attemptCount", "lastAttemptAt", "processingLeaseOwner", "processingLeaseExpiresAt", "ZSCORE", "ZADD", "KEEPTTL");
        scripts[RedisWebhookOperation.Claim].Should().NotContain("tonumber(record.processingLeaseExpiresAt)");
        scripts[RedisWebhookOperation.Release].Should().ContainAll("processingLeaseOwner", "ZADD", "KEEPTTL");
        scripts[RedisWebhookOperation.MarkProcessed].Should().ContainAll("processingLeaseOwner", "ZREM", "KEEPTTL");
        scripts[RedisWebhookOperation.MarkFailed].Should().ContainAll("processingLeaseOwner", "ZREM", "KEEPTTL");
        scripts[RedisWebhookOperation.Update].Should().ContainAll("processingLeaseOwner", "KEEPTTL");
        scripts[RedisWebhookOperation.Recover].Should().ContainAll("ZSCORE", "ZREM");
        scripts[RedisWebhookOperation.Recover].Should().NotContain("tonumber(record.processingLeaseExpiresAt)");
        scripts.Values.Should().OnlyContain(script => !script.Contains("raw-body-hash", StringComparison.Ordinal));
        scripts.Values.Where(script => script.Contains("KEEPTTL", StringComparison.Ordinal)).Should().OnlyContain(script => !script.Contains("PEXPIRE", StringComparison.Ordinal));
    }

    [Fact]
    public void AddWebhookKitRedis_MultiplexerOverload_RegistersRedisStore()
    {
        var connection = CreateConnectionMultiplexer();
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(Start));
        services.AddWebhookKitRedis(connection);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookStore>().Should().BeOfType<RedisWebhookStore>();
        provider.GetRequiredService<IConnectionMultiplexer>().Should().BeSameAs(connection);
    }

    [Fact]
    public void AddWebhookKitRedis_MultiplexerOverload_PreservesCustomRegistrationMadeAfterCall()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<IWebhookStore>();
        services.AddWebhookKitRedis(CreateConnectionMultiplexer());
        services.AddSingleton(custom);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void AddWebhookKitRedis_ConnectionStringOverload_RegistersLazyRedisStoreWithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddWebhookKitRedis("127.0.0.1:1,abortConnect=true");

        var connectionDescriptor = services.Single(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer));
        var storeDescriptor = services.Last(descriptor => descriptor.ServiceType == typeof(IWebhookStore));

        connectionDescriptor.ImplementationFactory.Should().NotBeNull();
        storeDescriptor.ImplementationType.Should().Be<RedisWebhookStore>();
    }

    [Fact]
    public void AddWebhookKitRedis_ConnectionStringOverload_PreservesCustomRegistrationMadeAfterCall()
    {
        var services = new ServiceCollection();
        var custom = Substitute.For<IWebhookStore>();
        services.AddWebhookKitRedis("127.0.0.1:1,abortConnect=true");
        services.AddSingleton(custom);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWebhookStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void AddWebhookKitRedis_ConfiguresStableCustomKeyPrefix()
    {
        var services = new ServiceCollection();
        services.AddWebhookKitRedis(CreateConnectionMultiplexer(), options =>
        {
            options.KeyPrefix = "tenant-a";
            options.DeduplicationRetention = TimeSpan.FromDays(8);
            options.RecordRetention = TimeSpan.FromDays(31);
        });
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RedisWebhookStoreOptions>>().Value;

        options.KeyPrefix.Should().Be("tenant-a");
        options.DeduplicationRetention.Should().Be(TimeSpan.FromDays(8));
        options.RecordRetention.Should().Be(TimeSpan.FromDays(31));
    }

    private static RedisWebhookStore CreateStore(
        StatefulRedisDatabase database,
        RedisWebhookStoreOptions? options = null,
        IWebhookClock? clock = null)
    {
        return new RedisWebhookStore(
            database,
            Microsoft.Extensions.Options.Options.Create(options ?? new RedisWebhookStoreOptions { KeyPrefix = "test" }),
            clock ?? new FakeWebhookClock(Start));
    }

    private static IConnectionMultiplexer CreateConnectionMultiplexer()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        return connection;
    }

    private static WebhookRecord CreateRecord(
        string id = DefaultWebhookId,
        string provider = "stripe",
        string? eventId = "evt_1",
        string? deduplicationKey = null)
    {
        return new WebhookRecord
        {
            Id = id,
            Provider = provider,
            EventId = eventId,
            DeduplicationKey = deduplicationKey ?? $"{provider.ToLowerInvariant()}:{eventId}",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>> { ["X-Test"] = new[] { "original" } },
            RawBody = [1, 2, 3],
            ReceivedAt = Start,
            Status = WebhookProcessingStatus.Received
        };
    }

    private static WebhookRecord CreateCompleteRecord()
    {
        return new WebhookRecord
        {
            Id = DefaultWebhookId,
            CorrelationId = "correlation-1",
            Provider = "stripe",
            EventId = "evt_complete",
            DeduplicationKey = "stripe:evt_complete",
            EventType = "payment.succeeded",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Signature"] = new[] { "signature-one", "signature-two" },
                ["X-Trace"] = new[] { "trace-1" }
            },
            ContentType = "application/json",
            ContentLength = 5,
            RawBody = [0, 1, 2, 254, 255],
            ReceivedAt = Start,
            ProviderTimestamp = Start.AddSeconds(-2),
            Status = WebhookProcessingStatus.Processing,
            AttemptCount = 4,
            LastAttemptAt = Start.AddSeconds(-1),
            ProcessingLeaseOwner = "worker-1",
            ProcessingLeaseExpiresAt = Start.AddMinutes(5),
            ProcessedAt = Start.AddSeconds(-3),
            FailedAt = Start.AddSeconds(-4),
            FailureReason = "handler-failed",
            FailureCode = "handler-failed"
        };
    }

    private static string Id(int value)
    {
        return "01J" + value.ToString("D23", CultureInfo.InvariantCulture);
    }

    private static string HashForStripeEvent(string eventId)
    {
        return eventId switch
        {
            "evt_1" => DefaultDeduplicationHash,
            "evt_second" => "1959c40477a392a190db23d4166b85a18daf3386b6b038f7628286d609a91a73",
            "evt:{secret}:raw-body-hash" => "80879d716997649ad7d518276021dc6c37df9ac6e8bdd75ae3895580a881bd63",
            _ => throw new ArgumentOutOfRangeException(nameof(eventId))
        };
    }

    private static string CreateSerializedTerminalRecord(string id)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["correlationId"] = null,
            ["provider"] = "stripe",
            ["eventId"] = "evt_processed",
            ["deduplicationKey"] = "stripe:evt_processed",
            ["eventType"] = "payment.processed",
            ["httpMethod"] = "POST",
            ["requestPath"] = "/webhooks/stripe",
            ["headers"] = new JsonObject(),
            ["contentType"] = "application/json",
            ["contentLength"] = 0,
            ["rawBody"] = null,
            ["receivedAt"] = Start,
            ["providerTimestamp"] = null,
            ["status"] = (int)WebhookProcessingStatus.Processed,
            ["attemptCount"] = 1,
            ["lastAttemptAt"] = Start,
            ["processingLeaseOwner"] = null,
            ["processingLeaseExpiresAt"] = null,
            ["processedAt"] = Start,
            ["failedAt"] = null,
            ["failureReason"] = null,
            ["failureCode"] = null
        }.ToJsonString();
    }

    private sealed class StatefulRedisDatabase : IRedisWebhookDatabase
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, double> _index = new(StringComparer.Ordinal);
        private readonly List<RedisWebhookCommand> _commands = [];
        private readonly List<SortedSetRead> _sortedSetReads = [];
        private readonly object _gate = new();
        private DateTimeOffset _now = Start;
        private CancellationTokenSource? _cancelAfterNextExecute;

        public IReadOnlyList<RedisWebhookCommand> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _commands.ToArray();
                }
            }
        }

        public SortedSetRead[] SortedSetReads
        {
            get
            {
                lock (_gate)
                {
                    return _sortedSetReads.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Keys
        {
            get
            {
                lock (_gate)
                {
                    RemoveExpired();
                    return _entries.Keys.ToArray();
                }
            }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate)
            {
                _now += duration;
                RemoveExpired();
            }
        }

        public void CancelAfterNextExecute(CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                _cancelAfterNextExecute = cancellation;
            }
        }

        public void ClearCommands()
        {
            lock (_gate)
            {
                _commands.Clear();
                _sortedSetReads.Clear();
            }
        }

        public string[] GetIndexMembers()
        {
            lock (_gate)
            {
                return _index.Keys.ToArray();
            }
        }

        public TimeSpan? GetTimeToLive(string key)
        {
            lock (_gate)
            {
                RemoveExpired();
                return _entries.TryGetValue(key, out var entry) && entry.ExpiresAt.HasValue
                    ? entry.ExpiresAt.Value - _now
                    : null;
            }
        }

        public string? GetRawValue(string key)
        {
            lock (_gate)
            {
                RemoveExpired();
                return _entries.TryGetValue(key, out var entry) ? entry.Value : null;
            }
        }

        public void RemoveKey(string key)
        {
            lock (_gate)
            {
                _entries.Remove(key);
            }
        }

        public void SeedKey(string key, string value, TimeSpan? timeToLive = null)
        {
            lock (_gate)
            {
                _entries[key] = new Entry(value, timeToLive.HasValue ? _now.Add(timeToLive.Value) : null);
            }
        }

        public void SeedIndexMember(string id, double score = double.NegativeInfinity)
        {
            lock (_gate)
            {
                _index[id] = score;
            }
        }

        public void SeedRawRecord(string id, string payload)
        {
            SeedKey($"test:record:{id}", payload);
        }

        public ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetRawValue(key));
        }

        public ValueTask<IReadOnlyList<string>> GetSortedSetRangeByScoreAsync(
            string key,
            double minimumScore,
            double maximumScore,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                RemoveExpired();
                _sortedSetReads.Add(new SortedSetRead(key, minimumScore, maximumScore, limit));
                var values = _index
                    .Where(entry => entry.Value >= minimumScore && entry.Value <= maximumScore)
                    .OrderBy(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(entry => entry.Key)
                    .ToArray();
                return ValueTask.FromResult<IReadOnlyList<string>>(values);
            }
        }

        public ValueTask<RedisWebhookCommandResult> ExecuteAsync(
            RedisWebhookCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource? cancellation;
            RedisWebhookCommandResult result;
            lock (_gate)
            {
                cancellation = _cancelAfterNextExecute;
                _cancelAfterNextExecute = null;
                _commands.Add(command);
                result = ExecuteCore(command);
            }

            cancellation?.Cancel();
            return ValueTask.FromResult(result);
        }

        private RedisWebhookCommandResult ExecuteCore(RedisWebhookCommand command)
        {
            return command.Operation switch
            {
                RedisWebhookOperation.Create => Create(command),
                RedisWebhookOperation.Claim => Claim(command),
                RedisWebhookOperation.Release => Release(command),
                RedisWebhookOperation.MarkProcessed => MarkProcessed(command),
                RedisWebhookOperation.MarkFailed => MarkFailed(command),
                RedisWebhookOperation.Update => Update(command),
                RedisWebhookOperation.Recover => Recover(command),
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
        }

        private RedisWebhookCommandResult Create(RedisWebhookCommand command)
        {
            RemoveExpired();
            var id = command.Values[0];
            if (_entries.ContainsKey(command.Keys[0]) || _entries.ContainsKey(command.Keys[1]) || _index.ContainsKey(id))
            {
                return Result(0);
            }

            var deduplicationTtl = ParseDuration(command.Values[2]);
            var recordTtl = ParseDuration(command.Values[3]);
            _entries[command.Keys[0]] = new Entry(id, _now.Add(deduplicationTtl));
            _entries[command.Keys[1]] = new Entry(command.Values[1], _now.Add(recordTtl));
            _index[id] = ParseDouble(command.Values[4]);
            return Result(1);
        }

        private RedisWebhookCommandResult Claim(RedisWebhookCommand command)
        {
            if (!_entries.TryGetValue(command.Keys[0], out var entry))
            {
                return Result(0);
            }

            var record = ParseObject(entry.Value);
            var status = record["status"]!.GetValue<int>();
            var now = ParseLong(command.Values[1]);
            var id = record["id"]!.GetValue<string>();
            var claimable = status == (int)WebhookProcessingStatus.Received;
            if (status == (int)WebhookProcessingStatus.Processing && _index.TryGetValue(id, out var score))
            {
                claimable = score == double.NegativeInfinity || ToUnixMilliseconds(score) <= now;
            }

            if (!claimable)
            {
                return Result(0);
            }

            record["status"] = (int)WebhookProcessingStatus.Processing;
            record["processingLeaseOwner"] = command.Values[0];
            record["processingLeaseExpiresAt"] = command.Values[4];
            record["attemptCount"] = record["attemptCount"]!.GetValue<int>() + 1;
            record["lastAttemptAt"] = command.Values[3];
            record["processedAt"] = null;
            record["failedAt"] = null;
            record["failureReason"] = null;
            record["failureCode"] = null;
            ReplaceValue(command.Keys[0], record.ToJsonString());
            _index[record["id"]!.GetValue<string>()] = ParseDouble(command.Values[2]);
            return Result(1);
        }

        private RedisWebhookCommandResult Release(RedisWebhookCommand command)
        {
            if (!_entries.TryGetValue(command.Keys[0], out var entry))
            {
                return Result(0);
            }

            var record = ParseObject(entry.Value);
            if (!IsOwnedProcessing(record, command.Values[0]))
            {
                return Result(0);
            }

            record["status"] = (int)WebhookProcessingStatus.Received;
            record["processingLeaseOwner"] = null;
            record["processingLeaseExpiresAt"] = null;
            record["processedAt"] = null;
            record["failedAt"] = null;
            record["failureReason"] = null;
            record["failureCode"] = null;
            ReplaceValue(command.Keys[0], record.ToJsonString());
            _index[record["id"]!.GetValue<string>()] = double.NegativeInfinity;
            return Result(1);
        }

        private RedisWebhookCommandResult MarkProcessed(RedisWebhookCommand command)
        {
            if (!_entries.TryGetValue(command.Keys[0], out var entry))
            {
                return Result(0);
            }

            var record = ParseObject(entry.Value);
            if (!IsOwnedProcessing(record, command.Values[0]))
            {
                return Result(0);
            }

            record["status"] = (int)WebhookProcessingStatus.Processed;
            record["processedAt"] = command.Values[1];
            record["failedAt"] = null;
            record["failureReason"] = null;
            record["failureCode"] = null;
            record["processingLeaseOwner"] = null;
            record["processingLeaseExpiresAt"] = null;
            ReplaceValue(command.Keys[0], record.ToJsonString());
            _index.Remove(record["id"]!.GetValue<string>());
            return Result(1);
        }

        private RedisWebhookCommandResult MarkFailed(RedisWebhookCommand command)
        {
            if (!_entries.TryGetValue(command.Keys[0], out var entry))
            {
                return Result(0);
            }

            var record = ParseObject(entry.Value);
            if (!IsOwnedProcessing(record, command.Values[0]))
            {
                return Result(0);
            }

            record["status"] = (int)WebhookProcessingStatus.Failed;
            record["failedAt"] = command.Values[1];
            record["processedAt"] = null;
            record["failureReason"] = command.Values[2].Length == 0 ? null : command.Values[2];
            record["failureCode"] = command.Values[3].Length == 0 ? null : command.Values[3];
            record["processingLeaseOwner"] = null;
            record["processingLeaseExpiresAt"] = null;
            ReplaceValue(command.Keys[0], record.ToJsonString());
            _index.Remove(record["id"]!.GetValue<string>());
            return Result(1);
        }

        private RedisWebhookCommandResult Update(RedisWebhookCommand command)
        {
            if (!_entries.TryGetValue(command.Keys[0], out var entry))
            {
                return Result(-1);
            }

            var current = ParseObject(entry.Value);
            var incoming = ParseObject(command.Values[0]);
            if (!SameText(current["id"], incoming["id"]) ||
                !SameText(current["provider"], incoming["provider"]) ||
                !SameText(current["deduplicationKey"], incoming["deduplicationKey"]))
            {
                return Result(-2);
            }

            var currentStatus = current["status"]!.GetValue<int>();
            var incomingStatus = incoming["status"]!.GetValue<int>();
            if (currentStatus == (int)WebhookProcessingStatus.Processing &&
                !SameText(current["processingLeaseOwner"], incoming["processingLeaseOwner"]))
            {
                return Result(0);
            }

            if (current["attemptCount"]!.GetValue<int>() > incoming["attemptCount"]!.GetValue<int>())
            {
                return Result(-2);
            }

            if (IsTerminal(currentStatus) && currentStatus != incomingStatus)
            {
                return Result(0);
            }

            if (currentStatus == (int)WebhookProcessingStatus.Received &&
                incomingStatus == (int)WebhookProcessingStatus.Processing)
            {
                return Result(0);
            }

            if (incomingStatus == (int)WebhookProcessingStatus.Processing &&
                currentStatus != (int)WebhookProcessingStatus.Processing)
            {
                return Result(0);
            }

            ReplaceValue(command.Keys[0], command.Values[0]);
            var id = incoming["id"]!.GetValue<string>();
            if (incomingStatus == (int)WebhookProcessingStatus.Received ||
                (incomingStatus == (int)WebhookProcessingStatus.Processing && incoming["processingLeaseExpiresAt"] is null))
            {
                _index[id] = double.NegativeInfinity;
            }
            else if (incomingStatus == (int)WebhookProcessingStatus.Processing)
            {
                _index[id] = ToUnixMilliseconds(incoming["processingLeaseExpiresAt"]!.GetValue<DateTimeOffset>());
            }
            else
            {
                _index.Remove(id);
            }

            return Result(1);
        }

        private RedisWebhookCommandResult Recover(RedisWebhookCommand command)
        {
            var cutoff = ParseLong(command.Values[0]);
            var recovered = new List<string>();
            for (var index = 0; index < command.Keys.Count - 1; index++)
            {
                var id = command.Values[index + 1];
                if (!_entries.TryGetValue(command.Keys[index + 1], out var entry))
                {
                    _index.Remove(id);
                    continue;
                }

                JsonObject record;
                try
                {
                    record = ParseObject(entry.Value);
                }
                catch (JsonException)
                {
                    _index.Remove(id);
                    continue;
                }

                var status = record["status"]!.GetValue<int>();
                if (IsTerminal(status))
                {
                    _index.Remove(id);
                    continue;
                }

                if (status == (int)WebhookProcessingStatus.Received)
                {
                    recovered.Add(entry.Value);
                    continue;
                }

                if (_index.TryGetValue(id, out var score) &&
                    (score == double.NegativeInfinity || ToUnixMilliseconds(score) <= cutoff))
                {
                    recovered.Add(entry.Value);
                }
            }

            return new RedisWebhookCommandResult(recovered.Count, recovered);
        }

        private void ReplaceValue(string key, string value)
        {
            var entry = _entries[key];
            _entries[key] = entry with { Value = value };
        }

        private void RemoveExpired()
        {
            foreach (var key in _entries
                .Where(entry => entry.Value.ExpiresAt.HasValue && entry.Value.ExpiresAt.Value <= _now)
                .Select(entry => entry.Key)
                .ToArray())
            {
                _entries.Remove(key);
            }
        }

        private static bool IsOwnedProcessing(JsonObject record, string owner)
        {
            return record["status"]!.GetValue<int>() == (int)WebhookProcessingStatus.Processing &&
                SameText(record["processingLeaseOwner"], owner);
        }

        private static bool SameText(JsonNode? left, JsonNode? right)
        {
            if (left is null || right is null)
            {
                return left is null && right is null;
            }

            return left.GetValue<string>() == right.GetValue<string>();
        }

        private static bool IsTerminal(int status)
        {
            return status is (int)WebhookProcessingStatus.Processed or
                (int)WebhookProcessingStatus.Failed or
                (int)WebhookProcessingStatus.Ignored or
                (int)WebhookProcessingStatus.Duplicate;
        }

        private static JsonObject ParseObject(string value)
        {
            return JsonNode.Parse(value)!.AsObject();
        }

        private static RedisWebhookCommandResult Result(long value)
        {
            return new RedisWebhookCommandResult(value, Array.Empty<string>());
        }

        private static TimeSpan ParseDuration(string value)
        {
            return TimeSpan.FromMilliseconds(ParseLong(value));
        }

        private static long ParseLong(string value)
        {
            return long.Parse(value, CultureInfo.InvariantCulture);
        }

        private static double ParseDouble(string value)
        {
            return value == "-inf" ? double.NegativeInfinity : double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static long ToUnixMilliseconds(DateTimeOffset value)
        {
            return value.ToUnixTimeMilliseconds();
        }

        private static long ToUnixMilliseconds(double value)
        {
            return checked((long)value);
        }

        private sealed record Entry(string Value, DateTimeOffset? ExpiresAt);

        public sealed record SortedSetRead(string Key, double MinimumScore, double MaximumScore, int Limit);
    }
}

#pragma warning restore CA1707
