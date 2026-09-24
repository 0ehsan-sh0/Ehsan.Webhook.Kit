using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.EntityFrameworkCore;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.EntityFrameworkCore.Tests;

public sealed class EfCoreConcurrencyTests
{
    private const string Provider = "concurrency-ef";
    private const string EventId = "evt-concurrency-ef";
    private const string EventType = "concurrency.event";
    private const string EventIdHeader = "X-Concurrency-Event-Id";
    private const string EventTypeHeader = "X-Concurrency-Event-Type";
    private const string TimestampHeader = "X-Concurrency-Timestamp";
    private const string SignatureHeader = "X-Concurrency-Signature";
    private const string Secret = "concurrency-ef-secret";
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task IngestionService_ConcurrentDuplicateDeliveries_UsesSqliteAuthorityAndProcessesOne(int level)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        var storeProbe = new StoreProbe();
        var handlerProbe = new HandlerProbe();
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(options => options.UseSqlite(database.ConnectionString));
        services.AddWebhookKit(ConfigureProvider);
        services.AddSingleton<IWebhookClock>(clock);
        services.AddScoped<IWebhookStore>(serviceProvider => new CountingWebhookStore(
            new EfCoreWebhookStore<TestContext>(serviceProvider.GetRequiredService<TestContext>(), clock),
            storeProbe));
        services.AddSingleton(handlerProbe);
        services.AddWebhookHandler<CountingHandler>(EventType);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var idGenerator = new WebhookIdGenerator(clock);
        var webhookIds = Enumerable.Range(0, level).Select(_ => idGenerator.Create()).ToArray();
        var body = BodyBytes();
        var signatureGenerator = new WebhookSignatureGenerator(Secret);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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

        await using var verificationContext = database.CreateIndependentContext();
        var rowCount = await verificationContext.Set<WebhookEntity>()
            .CountAsync()
            .WaitAsync(TestTimeout);
        rowCount.Should().Be(1);
        var verificationStore = new EfCoreWebhookStore<TestContext>(verificationContext, clock);
        var records = new List<WebhookRecord?>();
        foreach (var webhookId in webhookIds)
        {
            records.Add(await verificationStore.GetByWebhookIdAsync(webhookId).AsTask().WaitAsync(TestTimeout));
        }

        records.Count(record => record is not null).Should().Be(1);
        var stored = records.Single(record => record is not null)!;
        stored.Status.Should().Be(WebhookProcessingStatus.Processed);
        stored.EventId.Should().Be(EventId);
        stored.AttemptCount.Should().Be(1);
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
            RequestPath = "/concurrency/ef",
            Headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
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

    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyWebhookConfiguration();
        }
    }

    private sealed class SqliteTestDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private readonly SqliteConnection _anchorConnection;

        private SqliteTestDatabase(string path, string connectionString, SqliteConnection anchorConnection)
        {
            _path = path;
            ConnectionString = connectionString;
            _anchorConnection = anchorConnection;
        }

        public string ConnectionString { get; }

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"webhookkit-concurrency-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30
            }.ToString();
            var anchorConnection = new SqliteConnection(connectionString);
            await anchorConnection.OpenAsync();
            var database = new SqliteTestDatabase(path, connectionString, anchorConnection);
            await using var context = database.CreateIndependentContext();
            await context.Database.EnsureCreatedAsync();
            await using var command = anchorConnection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
            await command.ExecuteNonQueryAsync();
            return database;
        }

        public TestContext CreateIndependentContext()
        {
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseSqlite(ConnectionString)
                .Options;
            return new TestContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _anchorConnection.DisposeAsync();
            foreach (var path in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
