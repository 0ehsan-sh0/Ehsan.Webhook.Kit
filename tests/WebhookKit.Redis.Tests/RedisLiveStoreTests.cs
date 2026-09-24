using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebhookKit.Abstractions;
using WebhookKit.Redis;
using WebhookKit.Testing;
using Xunit;

#pragma warning disable CA1707

namespace WebhookKit.Redis.Tests;

public sealed class RedisLiveStoreTests
{
    private const string ConnectionVariable = "WEBHOOKKIT_REDIS_CONNECTION";
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromSeconds(5);

    [RedisLiveFact]
    [Trait("Category", "LiveRedis")]
    public async Task RealRedisStore_RoundTripsEveryFieldAndRejectsDuplicate()
    {
        var prefix = NewPrefix("roundtrip");
        using var connection = await ConnectAsync(GetConnectionString());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var store = CreateStore(connection, prefix, new FakeWebhookClock(Start));
        var record = CreateCompleteRecord(Id(101), "evt-live-roundtrip");

        try
        {
            (await store.TryCreateAsync(record, cancellation.Token)).Should().BeTrue();

            var duplicate = CreateRecord(Id(102), "evt-live-roundtrip");
            (await store.TryCreateAsync(duplicate, cancellation.Token)).Should().BeFalse();

            var byEvent = await store.GetAsync("STRIPE", "evt-live-roundtrip", cancellation.Token);
            byEvent.Should().NotBeNull();
            byEvent!.Id.Should().Be(record.Id);
            byEvent.CorrelationId.Should().Be(record.CorrelationId);
            byEvent.Provider.Should().Be(record.Provider);
            byEvent.EventId.Should().Be(record.EventId);
            byEvent.DeduplicationKey.Should().Be(record.DeduplicationKey);
            byEvent.EventType.Should().Be(record.EventType);
            byEvent.HttpMethod.Should().Be(record.HttpMethod);
            byEvent.RequestPath.Should().Be(record.RequestPath);
            byEvent.Headers["X-Signature"].Should().Equal("signature-one", "signature-two");
            byEvent.Headers["X-Trace"].Should().Equal("trace-one");
            byEvent.ContentType.Should().Be(record.ContentType);
            byEvent.ContentLength.Should().Be(record.ContentLength);
            byEvent.RawBody.Should().Equal(0, 1, 2, 254, 255);
            byEvent.ReceivedAt.Should().Be(record.ReceivedAt);
            byEvent.ProviderTimestamp.Should().Be(record.ProviderTimestamp);
            byEvent.Status.Should().Be(record.Status);
            byEvent.AttemptCount.Should().Be(record.AttemptCount);
            byEvent.LastAttemptAt.Should().Be(record.LastAttemptAt);
            byEvent.ProcessingLeaseOwner.Should().BeNull();
            byEvent.ProcessingLeaseExpiresAt.Should().BeNull();
            byEvent.ProcessedAt.Should().BeNull();
            byEvent.FailedAt.Should().BeNull();
            byEvent.FailureReason.Should().BeNull();
            byEvent.FailureCode.Should().BeNull();

            byEvent.RawBody![0] = 99;
            var second = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            second.Should().NotBeNull();
            second!.RawBody.Should().Equal(0, 1, 2, 254, 255);
            second.Headers["X-Signature"].Should().Equal("signature-one", "signature-two");
            (await store.GetAsync("stripe", "evt-live-roundtrip", cancellation.Token))!.Id.Should().Be(record.Id);
            (await store.GetByWebhookIdAsync(duplicate.Id, cancellation.Token)).Should().BeNull();
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    [RedisLiveFact]
    [Trait("Category", "LiveRedis")]
    public async Task RealRedisStore_ProtectsLeaseTransitionsAndStaleOwners()
    {
        var prefix = NewPrefix("transitions");
        var clock = new FakeWebhookClock(Start);
        using var connection = await ConnectAsync(GetConnectionString());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var store = CreateStore(connection, prefix, clock);
        var record = CreateRecord(Id(201), "evt-live-transitions");

        try
        {
            (await store.TryCreateAsync(record, cancellation.Token)).Should().BeTrue();
            (await store.TryClaimAsync(record.Id, "worker-1", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeTrue();
            (await store.TryClaimAsync(record.Id, "worker-2", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeFalse();
            (await store.ReleaseAsync(record.Id, "worker-2", cancellation.Token)).Should().BeFalse();
            (await store.ReleaseAsync(record.Id, "worker-1", cancellation.Token)).Should().BeTrue();

            var released = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            released.Should().NotBeNull();
            released!.Status.Should().Be(WebhookProcessingStatus.Received);
            released.ProcessingLeaseOwner.Should().BeNull();
            released.ProcessingLeaseExpiresAt.Should().BeNull();

            (await store.TryClaimAsync(record.Id, "worker-2", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeTrue();
            var stale = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            stale.Should().NotBeNull();
            stale!.FailureReason = "stale-failure";

            clock.Advance(TimeSpan.FromMinutes(2));
            (await store.TryClaimAsync(record.Id, "worker-3", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeTrue();
            (await store.ReleaseAsync(record.Id, "worker-2", cancellation.Token)).Should().BeFalse();
            (await store.MarkProcessedAsync(record.Id, "worker-2", clock.UtcNow, cancellation.Token)).Should().BeFalse();
            (await store.MarkFailedAsync(record.Id, "worker-2", clock.UtcNow, "stale-failure", cancellation.Token)).Should().BeFalse();
            await store.UpdateAsync(stale, cancellation.Token);

            var current = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            current.Should().NotBeNull();
            current!.ProcessingLeaseOwner.Should().Be("worker-3");
            current.FailureReason.Should().BeNull();
            current.FailureReason = "retrying";
            await store.UpdateAsync(current, cancellation.Token);

            var updated = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            updated.Should().NotBeNull();
            updated!.FailureReason.Should().Be("retrying");
            updated.AttemptCount.Should().Be(3);
            (await store.MarkProcessedAsync(record.Id, "worker-3", clock.UtcNow, cancellation.Token)).Should().BeTrue();

            var processed = await store.GetByWebhookIdAsync(record.Id, cancellation.Token);
            processed.Should().NotBeNull();
            processed!.Status.Should().Be(WebhookProcessingStatus.Processed);
            processed.ProcessedAt.Should().Be(clock.UtcNow);
            processed.ProcessingLeaseOwner.Should().BeNull();
            processed.ProcessingLeaseExpiresAt.Should().BeNull();
            (await store.GetRecoverableAsync(clock.UtcNow, TimeSpan.Zero, 10, cancellation.Token)).Should().BeEmpty();
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    [RedisLiveFact]
    [Trait("Category", "LiveRedis")]
    public async Task RealRedisStore_MarksFailedAndRecoversWaitingAndExpiredLeases()
    {
        var prefix = NewPrefix("recovery");
        var clock = new FakeWebhookClock(Start);
        using var connection = await ConnectAsync(GetConnectionString());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var store = CreateStore(connection, prefix, clock);
        var failed = CreateRecord(Id(301), "evt-live-failed");
        var active = CreateRecord(Id(302), "evt-live-active");
        var waiting = CreateRecord(Id(303), "evt-live-waiting");

        try
        {
            (await store.TryCreateAsync(failed, cancellation.Token)).Should().BeTrue();
            (await store.TryCreateAsync(active, cancellation.Token)).Should().BeTrue();
            (await store.TryCreateAsync(waiting, cancellation.Token)).Should().BeTrue();
            (await store.TryClaimAsync(failed.Id, "worker-1", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeTrue();
            (await store.MarkFailedAsync(failed.Id, "worker-2", clock.UtcNow, "handler failed with secret details", cancellation.Token)).Should().BeFalse();
            (await store.MarkFailedAsync(failed.Id, "worker-1", clock.UtcNow, "handler failed with secret details", cancellation.Token)).Should().BeTrue();

            var failedRecord = await store.GetByWebhookIdAsync(failed.Id, cancellation.Token);
            failedRecord.Should().NotBeNull();
            failedRecord!.Status.Should().Be(WebhookProcessingStatus.Failed);
            failedRecord.FailureReason.Should().Be("processing-failed");
            failedRecord.FailureCode.Should().Be("processing-failed");
            failedRecord.ProcessingLeaseOwner.Should().BeNull();

            (await store.TryClaimAsync(active.Id, "worker-1", TimeSpan.FromMinutes(1), cancellation.Token)).Should().BeTrue();
            var recoverable = await store.GetRecoverableAsync(
                Start.AddMinutes(2),
                TimeSpan.FromMinutes(1),
                10,
                cancellation.Token);

            recoverable.Select(record => record.Id).Should().BeEquivalentTo([active.Id, waiting.Id]);
            recoverable.Should().OnlyContain(record =>
                record.Status == WebhookProcessingStatus.Received ||
                record.Status == WebhookProcessingStatus.Processing);
            (await store.GetByWebhookIdAsync(failed.Id, cancellation.Token))!.Status.Should().Be(WebhookProcessingStatus.Failed);
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    [RedisLiveFact]
    [Trait("Category", "LiveRedis")]
    public async Task RealRedisStore_AcceptsGeneratorCompatibleUlidCharacters()
    {
        var prefix = NewPrefix("ulid");
        using var connection = await ConnectAsync(GetConnectionString());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var store = CreateStore(connection, prefix, new FakeWebhookClock(Start));
        var record = CreateRecord("01M38BCG005GH9WZPZCBF9S810", "evt-live-ulid");

        try
        {
            (await store.TryCreateAsync(record, cancellation.Token)).Should().BeTrue();
            (await store.GetByWebhookIdAsync(record.Id, cancellation.Token)).Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    [RedisLiveFact]
    [Trait("Category", "LiveRedis")]
    public async Task RealRedisStore_ExposesIndependentDeduplicationAndRecordTtls()
    {
        var prefix = NewPrefix("ttl");
        var deduplicationTtl = TimeSpan.FromMilliseconds(500);
        var recordTtl = TimeSpan.FromSeconds(2);
        using var connection = await ConnectAsync(GetConnectionString());
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var store = CreateStore(
            connection,
            prefix,
            new FakeWebhookClock(Start),
            deduplicationTtl,
            recordTtl);
        var record = CreateRecord(Id(401), "evt-live-ttl");

        try
        {
            (await store.TryCreateAsync(record, cancellation.Token)).Should().BeTrue();
            var database = connection.GetDatabase();
            var keys = GetKeys(connection, prefix);
            keys.Should().HaveCount(3);
            var deduplicationKey = keys.Single(key => key.ToString()!.Contains(":dedup:", StringComparison.Ordinal));
            var recordKey = keys.Single(key => key.ToString()!.Contains(":record:", StringComparison.Ordinal));

            var initialDeduplicationTtl = await database.KeyTimeToLiveAsync(deduplicationKey);
            var initialRecordTtl = await database.KeyTimeToLiveAsync(recordKey);
            initialDeduplicationTtl.Should().NotBeNull();
            initialRecordTtl.Should().NotBeNull();
            initialDeduplicationTtl!.Value.Should().BeGreaterThan(TimeSpan.Zero);
            initialDeduplicationTtl.Value.Should().BeLessThanOrEqualTo(deduplicationTtl);
            initialRecordTtl!.Value.Should().BeGreaterThan(initialDeduplicationTtl.Value);
            initialRecordTtl.Value.Should().BeLessThanOrEqualTo(recordTtl);

            await WaitForAsync(async () => !await database.KeyExistsAsync(deduplicationKey), ExpirationTimeout);
            (await store.GetAsync("stripe", "evt-live-ttl", cancellation.Token)).Should().BeNull();
            (await store.GetByWebhookIdAsync(record.Id, cancellation.Token)).Should().NotBeNull();

            await WaitForAsync(async () => !await database.KeyExistsAsync(recordKey), ExpirationTimeout);
            (await store.GetByWebhookIdAsync(record.Id, cancellation.Token)).Should().BeNull();
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    private static RedisWebhookStore CreateStore(
        ConnectionMultiplexer connection,
        string prefix,
        IWebhookClock clock,
        TimeSpan? deduplicationRetention = null,
        TimeSpan? recordRetention = null)
    {
        return new RedisWebhookStore(
            connection,
            Options.Create(new RedisWebhookStoreOptions
            {
                KeyPrefix = prefix,
                DeduplicationRetention = deduplicationRetention ?? TimeSpan.FromHours(1),
                RecordRetention = recordRetention ?? TimeSpan.FromHours(1)
            }),
            clock);
    }

    private static WebhookRecord CreateRecord(string id, string eventId)
    {
        return new WebhookRecord
        {
            Id = id,
            CorrelationId = $"correlation-{id}",
            Provider = "stripe",
            EventId = eventId,
            DeduplicationKey = $"stripe:{eventId}",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Test"] = new[] { "original" }
            },
            RawBody = [1, 2, 3],
            ContentType = "application/json",
            ContentLength = 3,
            ReceivedAt = Start,
            Status = WebhookProcessingStatus.Received
        };
    }

    private static WebhookRecord CreateCompleteRecord(string id, string eventId)
    {
        var body = new byte[] { 0, 1, 2, 254, 255 };
        return new WebhookRecord
        {
            Id = id,
            CorrelationId = $"correlation-{id}",
            Provider = "stripe",
            EventId = eventId,
            DeduplicationKey = $"stripe:{eventId}",
            EventType = "payment.succeeded",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Signature"] = new[] { "signature-one", "signature-two" },
                ["X-Trace"] = new[] { "trace-one" }
            },
            ContentType = "application/json",
            ContentLength = body.Length,
            RawBody = body,
            ReceivedAt = Start,
            ProviderTimestamp = Start.AddSeconds(-2),
            Status = WebhookProcessingStatus.Received
        };
    }

    private static string GetConnectionString()
    {
        return Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionVariable} to run the real Redis tests.");
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        return await ConnectionMultiplexer.ConnectAsync(connectionString).WaitAsync(TestTimeout);
    }

    private static string NewPrefix(string purpose)
    {
        return $"task36-{purpose}-{Guid.NewGuid():N}";
    }

    private static string Id(int value)
    {
        return "01J" + value.ToString("D23", CultureInfo.InvariantCulture);
    }

    private static RedisKey[] GetKeys(ConnectionMultiplexer connection, string prefix)
    {
        var database = connection.GetDatabase();
        return connection.GetEndPoints()
            .Select(endpoint => connection.GetServer(endpoint))
            .Where(server => server.IsConnected)
            .SelectMany(server => server.Keys(database.Database, $"{prefix}:*"))
            .Distinct()
            .ToArray();
    }

    private static async Task CleanupAsync(ConnectionMultiplexer connection, string prefix)
    {
        var keys = GetKeys(connection, prefix);
        if (keys.Length > 0)
        {
            await connection.GetDatabase().KeyDeleteAsync(keys).WaitAsync(TestTimeout);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"The Redis state did not converge within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RedisLiveFactAttribute : FactAttribute
{
    public RedisLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBHOOKKIT_REDIS_CONNECTION")))
        {
            Skip = "Set WEBHOOKKIT_REDIS_CONNECTION to run the real Redis store test.";
        }
    }
}
