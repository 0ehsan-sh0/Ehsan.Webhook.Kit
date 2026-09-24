using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Redis;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Redis.Tests;

public sealed class RedisConcurrencyTests
{
    private const string ConnectionVariable = "WEBHOOKKIT_REDIS_CONNECTION";
    private const string Provider = "concurrency-redis";
    private const string EventId = "evt-concurrency-redis";
    private const string EventType = "concurrency.event";
    private const string EventIdHeader = "X-Concurrency-Event-Id";
    private const string EventTypeHeader = "X-Concurrency-Event-Type";
    private const string TimestampHeader = "X-Concurrency-Timestamp";
    private const string SignatureHeader = "X-Concurrency-Signature";
    private const string Secret = "concurrency-redis-secret";
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(1);

    [RedisConcurrencyTheory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task RealRedisConcurrentDuplicateDeliveriesAcquiresOneClaimAndProcessesOne(int level)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionVariable} to run the real Redis concurrency test.");
        using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString).WaitAsync(TestTimeout);
        var prefix = $"concurrency-{Guid.NewGuid():N}";
        var clock = new FakeWebhookClock(Start);
        var storeProbe = new StoreProbe();
        var handlerProbe = new HandlerProbe();
        var options = Options.Create(new RedisWebhookStoreOptions
        {
            KeyPrefix = prefix,
            DeduplicationRetention = TimeSpan.FromHours(1),
            RecordRetention = TimeSpan.FromHours(1)
        });
        var innerStore = new RedisWebhookStore(connection, options, clock);
        var store = new CountingWebhookStore(innerStore, storeProbe);
        var services = new ServiceCollection();
        services.AddWebhookKit(ConfigureProvider);
        services.AddSingleton<IWebhookClock>(clock);
        services.AddSingleton<IWebhookStore>(store);
        services.AddSingleton(handlerProbe);
        services.AddWebhookHandler<CountingHandler>(EventType);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var idGenerator = new WebhookIdGenerator(clock);
        var webhookIds = Enumerable.Range(0, level).Select(_ => idGenerator.Create()).ToArray();
        var body = BodyBytes();
        var signatureGenerator = new WebhookSignatureGenerator(Secret);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            webhookIds.Should().HaveCount(level);
            webhookIds.Distinct(StringComparer.Ordinal).Should().HaveCount(level);
            foreach (var webhookId in webhookIds)
            {
                IsCanonicalUlid(webhookId).Should().BeTrue();
            }

            using var cancellation = new CancellationTokenSource(TestTimeout);
            var tasks = webhookIds.Select(async webhookId =>
            {
                await gate.Task.WaitAsync(TestTimeout);
                await using var scope = provider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
                return await service.IngestAsync(
                        CreateRequest(webhookId, body, signatureGenerator),
                        cancellation.Token)
                    .WaitAsync(TestTimeout);
            }).ToArray();

            gate.SetResult(true);
            var results = await Task.WhenAll(tasks).WaitAsync(TestTimeout);

            results.Should().HaveCount(level);
            results.Count(result => result.Status == WebhookIngestionStatus.Processed).Should().Be(1);
            results.Count(result => result.Status == WebhookIngestionStatus.Duplicate).Should().Be(level - 1);
            storeProbe.DeduplicationClaims.Should().Be(1);
            storeProbe.ProcessingClaims.Should().Be(1);
            handlerProbe.Executions.Should().Be(1);

            var records = await Task.WhenAll(webhookIds.Select(webhookId =>
                    innerStore.GetByWebhookIdAsync(webhookId).AsTask().WaitAsync(TestTimeout)))
                .WaitAsync(TestTimeout);
            records.Count(record => record is not null).Should().Be(1);
            var stored = records.Single(record => record is not null)!;
            stored.Status.Should().Be(WebhookProcessingStatus.Processed);
            stored.EventId.Should().Be(EventId);
            stored.AttemptCount.Should().Be(1);
        }
        finally
        {
            await CleanupAsync(connection, prefix);
        }
    }

    private static void ConfigureProvider(WebhookKitOptions options)
    {
        options.AddProvider(Provider, provider =>
        {
            provider.Signature.HeaderName = SignatureHeader;
            provider.Signature.Secret = Secret;
            provider.Timestamp.HeaderName = TimestampHeader;
            provider.EventIdHeaderName = EventIdHeader;
            provider.EventTypeHeaderName = EventTypeHeader;
        });
    }

    private static WebhookIngestionRequest CreateRequest(
        string webhookId,
        byte[] body,
        WebhookSignatureGenerator signatureGenerator)
    {
        var timestamp = Start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = signatureGenerator.Generate(body, timestamp);
        return new WebhookIngestionRequest
        {
            WebhookId = webhookId,
            Provider = Provider,
            HttpMethod = "POST",
            RequestPath = "/concurrency/redis",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [EventIdHeader] = [EventId],
                [EventTypeHeader] = [EventType],
                [TimestampHeader] = [timestamp],
                [SignatureHeader] = [signature]
            },
            RawBody = body.ToArray(),
            ContentType = "application/json",
            ContentLength = body.Length
        };
    }

    private static byte[] BodyBytes()
    {
        return Encoding.UTF8.GetBytes("{\"value\":42}");
    }

    private static bool IsCanonicalUlid(string value)
    {
        if (value.Length != 26 || value[0] is < '0' or > '7')
        {
            return false;
        }

        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        return value.All(alphabet.Contains);
    }

    private static async Task CleanupAsync(ConnectionMultiplexer connection, string prefix)
    {
        var database = connection.GetDatabase();
        var keys = connection.GetEndPoints()
            .Select(endpoint => connection.GetServer(endpoint))
            .Where(server => server.IsConnected)
            .SelectMany(server => server.Keys(database.Database, pattern: $"{prefix}:*"))
            .Distinct()
            .ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys).WaitAsync(TestTimeout);
        }
    }

    public sealed record Payload(int Value);

    public sealed class HandlerProbe
    {
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public void RecordExecution()
        {
            Interlocked.Increment(ref _executions);
        }
    }

    private sealed class StoreProbe
    {
        private int _deduplicationClaims;
        private int _processingClaims;

        public int DeduplicationClaims => Volatile.Read(ref _deduplicationClaims);

        public int ProcessingClaims => Volatile.Read(ref _processingClaims);

        public void RecordDeduplicationClaim()
        {
            Interlocked.Increment(ref _deduplicationClaims);
        }

        public void RecordProcessingClaim()
        {
            Interlocked.Increment(ref _processingClaims);
        }
    }

    public sealed class CountingHandler(HandlerProbe probe) : IWebhookHandler<Payload>
    {
        public Task HandleAsync(
            Payload eventData,
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.RecordExecution();
            return Task.CompletedTask;
        }
    }

    private sealed class CountingWebhookStore(IWebhookStore inner, StoreProbe probe) : IWebhookStore
    {
        public ValueTask<WebhookRecord?> GetAsync(
            string provider,
            string eventId,
            CancellationToken cancellationToken = default)
        {
            return inner.GetAsync(provider, eventId, cancellationToken);
        }

        public async ValueTask<bool> TryCreateAsync(
            WebhookRecord record,
            CancellationToken cancellationToken = default)
        {
            var acquired = await inner.TryCreateAsync(record, cancellationToken);
            if (acquired)
            {
                probe.RecordDeduplicationClaim();
            }

            return acquired;
        }

        public ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
        {
            return inner.UpdateAsync(record, cancellationToken);
        }

        public ValueTask<WebhookRecord?> GetByWebhookIdAsync(
            string webhookId,
            CancellationToken cancellationToken = default)
        {
            return inner.GetByWebhookIdAsync(webhookId, cancellationToken);
        }

        public async ValueTask<bool> TryClaimAsync(
            string webhookId,
            string leaseOwner,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            var claimed = await inner.TryClaimAsync(webhookId, leaseOwner, leaseDuration, cancellationToken);
            if (claimed)
            {
                probe.RecordProcessingClaim();
            }

            return claimed;
        }

        public ValueTask<bool> ReleaseAsync(
            string webhookId,
            string leaseOwner,
            CancellationToken cancellationToken = default)
        {
            return inner.ReleaseAsync(webhookId, leaseOwner, cancellationToken);
        }

        public ValueTask<bool> MarkProcessedAsync(
            string webhookId,
            string leaseOwner,
            DateTimeOffset processedAt,
            CancellationToken cancellationToken = default)
        {
            return inner.MarkProcessedAsync(webhookId, leaseOwner, processedAt, cancellationToken);
        }

        public ValueTask<bool> MarkFailedAsync(
            string webhookId,
            string leaseOwner,
            DateTimeOffset failedAt,
            string? failureReason,
            CancellationToken cancellationToken = default)
        {
            return inner.MarkFailedAsync(webhookId, leaseOwner, failedAt, failureReason, cancellationToken);
        }

        public ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(
            DateTimeOffset now,
            TimeSpan expiredLeaseAge,
            int limit,
            CancellationToken cancellationToken = default)
        {
            return inner.GetRecoverableAsync(now, expiredLeaseAge, limit, cancellationToken);
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RedisConcurrencyTheoryAttribute : TheoryAttribute
{
    public RedisConcurrencyTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBHOOKKIT_REDIS_CONNECTION")))
        {
            Skip = "Set WEBHOOKKIT_REDIS_CONNECTION to run the real Redis concurrency test.";
        }
    }
}
