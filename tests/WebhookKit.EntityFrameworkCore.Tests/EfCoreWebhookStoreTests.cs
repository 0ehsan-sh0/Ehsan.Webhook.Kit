using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.EntityFrameworkCore;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.EntityFrameworkCore.Tests;

public sealed class EfCoreWebhookStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyWebhookConfiguration_MapsKeysIndexesStorageAndConcurrency()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var entityType = context.Model.FindEntityType(typeof(WebhookEntity));

        entityType.Should().NotBeNull();
        entityType!.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("Id");
        entityType.FindProperty(nameof(WebhookEntity.DeduplicationKey))!.IsNullable.Should().BeFalse();
        entityType.FindProperty(nameof(WebhookEntity.RawBody))!.IsNullable.Should().BeTrue();
        entityType.FindProperty(nameof(WebhookEntity.HeadersJson))!.IsNullable.Should().BeFalse();
        entityType.FindProperty(nameof(WebhookEntity.Status))!.IsNullable.Should().BeFalse();
        entityType.FindProperty(nameof(WebhookEntity.ProcessingLeaseOwner))!.IsNullable.Should().BeTrue();
        entityType.FindProperty(nameof(WebhookEntity.ProcessingLeaseExpiresAt))!.IsNullable.Should().BeTrue();
        entityType.FindProperty(nameof(WebhookEntity.Version))!.IsConcurrencyToken.Should().BeTrue();
        entityType.FindProperty(nameof(WebhookEntity.Provider))!.GetMaxLength().Should().Be(128);
        entityType.FindProperty(nameof(WebhookEntity.DeduplicationKey))!.GetMaxLength().Should().Be(512);
        entityType.FindProperty(nameof(WebhookEntity.RequestPath))!.GetMaxLength().Should().Be(2048);

        var deduplicationIndex = entityType.GetIndexes()
            .Single(index => index.Properties.Count == 1 && index.Properties[0].Name == nameof(WebhookEntity.DeduplicationKey));
        deduplicationIndex.IsUnique.Should().BeTrue();
        entityType.GetIndexes().Should().Contain(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
            new[] { nameof(WebhookEntity.Status), nameof(WebhookEntity.ProcessingLeaseExpiresAt), nameof(WebhookEntity.ReceivedAt) }));

        await context.Database.EnsureCreatedAsync();
        await using var command = database.CreateCommand("SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'WebhookEntities' AND sql IS NOT NULL");
        var schema = (string?)await command.ExecuteScalarAsync();
        schema.Should().Contain("UNIQUE");
    }

    [Fact]
    public async Task TryCreateAsync_OneHundredConcurrentDuplicateKeys_AllowsExactlyOneRecord()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100)
            .Select(async index =>
            {
                await start.Task;
                await using var context = database.CreateIndependentContext();
                var store = new EfCoreWebhookStore<TestContext>(context, clock);
                return await store.TryCreateAsync(CreateRecord($"webhook-{index:D3}"));
            })
            .ToArray();

        start.SetResult(true);
        var results = await Task.WhenAll(tasks);

        results.Count(result => result).Should().Be(1);
        await using var verificationContext = database.CreateIndependentContext();
        verificationContext.Set<WebhookEntity>().Count().Should().Be(1);
    }

    [Fact]
    public async Task TryCreateAsync_ExactDuplicate_ReturnsFalse()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);

        (await store.TryCreateAsync(CreateRecord("webhook-first"))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("webhook-second"))).Should().BeFalse();
        context.Set<WebhookEntity>().Count().Should().Be(1);
    }

    [Fact]
    public async Task TryCreateAsync_ProviderScopedFallbackKey_RoundTripsWithoutEventId()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var record = new WebhookRecord
        {
            Id = "fallback-id",
            Provider = "stripe",
            EventId = null,
            DeduplicationKey = $"stripe:sha256:{new string('A', 64)}",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, string[]> { ["X-Test"] = ["value"] },
            RawBody = [1, 2, 3],
            ReceivedAt = Start
        };
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, new FakeWebhookClock(Start));

        (await store.TryCreateAsync(record)).Should().BeTrue();
        var stored = await store.GetByWebhookIdAsync(record.Id);

        stored!.EventId.Should().BeNull();
        stored.DeduplicationKey.Should().Be($"stripe:sha256:{new string('a', 64)}");
    }

    [Fact]
    public async Task TryCreateAsync_NonUniqueDatabaseFailure_Propagates()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContextWithInterceptor(new ThrowingSaveChangesInterceptor());
        var store = new EfCoreWebhookStore<TestContext>(context, new FakeWebhookClock(Start));

        Func<Task> act = async () => await store.TryCreateAsync(CreateRecord());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task TryCreateAsync_SqliteNonUniqueConstraint_Propagates()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContextWithInterceptor(new NonUniqueConstraintInterceptor());
        var store = new EfCoreWebhookStore<TestContext>(context, new FakeWebhookClock(Start));

        Func<Task> act = async () => await store.TryCreateAsync(CreateRecord());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task LookupMethods_ReturnIndependentCopiesOfMutableState()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        var original = CreateRecord("webhook-copy", rawBody: [1, 2, 3]);
        await using (var createContext = database.CreateContext())
        {
            var store = new EfCoreWebhookStore<TestContext>(createContext, clock);
            (await store.TryCreateAsync(original)).Should().BeTrue();
        }

        original.RawBody![0] = 9;
        original.Headers["X-Test"]![0] = "changed";

        await using var lookupContext = database.CreateContext();
        var lookupStore = new EfCoreWebhookStore<TestContext>(lookupContext, clock);
        var first = await lookupStore.GetByWebhookIdAsync("webhook-copy");
        first.Should().NotBeNull();
        first!.RawBody.Should().Equal(1, 2, 3);
        first.Headers["X-Test"].Should().Equal("original");
        first.RawBody![0] = 8;
        first.Headers["X-Test"][0] = "changed-again";

        var second = await lookupStore.GetAsync("stripe", "evt-1");
        second.Should().NotBeNull();
        second!.RawBody.Should().Equal(1, 2, 3);
        second.Headers["X-Test"].Should().Equal("original");
    }

    [Fact]
    public async Task RoundTrip_PreservesMetadataBodyCorrelationAttemptsAndSafeFailureFields()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        var record = CreateRecord("webhook-round-trip");
        var failed = new WebhookRecord
        {
            Id = record.Id,
            CorrelationId = "correlation-round-trip",
            Provider = record.Provider,
            EventId = record.EventId,
            DeduplicationKey = record.DeduplicationKey,
            EventType = "payment.succeeded",
            HttpMethod = "PUT",
            RequestPath = "/hooks/stripe",
            Headers = new Dictionary<string, string[]>
            {
                ["X-Signature"] = ["signature-value"],
                ["X-Multi"] = ["one", "two"]
            },
            ContentType = "application/json",
            ContentLength = 17,
            RawBody = [0, 255, 10, 13],
            ReceivedAt = Start,
            ProviderTimestamp = Start.AddSeconds(17),
            Status = WebhookProcessingStatus.Failed,
            AttemptCount = 2,
            LastAttemptAt = Start.AddSeconds(20),
            ProcessedAt = null,
            FailedAt = Start.AddSeconds(30),
            FailureReason = "handler-failed",
            FailureCode = "handler-failed"
        };

        await using (var createContext = database.CreateContext())
        {
            var store = new EfCoreWebhookStore<TestContext>(createContext, clock);
            (await store.TryCreateAsync(failed)).Should().BeTrue();
        }

        await using var readContext = database.CreateContext();
        var stored = await new EfCoreWebhookStore<TestContext>(readContext, clock).GetByWebhookIdAsync(failed.Id);

        stored.Should().NotBeNull();
        stored!.CorrelationId.Should().Be(failed.CorrelationId);
        stored.Provider.Should().Be(failed.Provider);
        stored.EventId.Should().Be(failed.EventId);
        stored.DeduplicationKey.Should().Be(failed.DeduplicationKey);
        stored.EventType.Should().Be(failed.EventType);
        stored.HttpMethod.Should().Be(failed.HttpMethod);
        stored.RequestPath.Should().Be(failed.RequestPath);
        stored.Headers.Should().BeEquivalentTo(failed.Headers);
        stored.ContentType.Should().Be(failed.ContentType);
        stored.ContentLength.Should().Be(failed.ContentLength);
        stored.RawBody.Should().Equal(failed.RawBody);
        stored.ReceivedAt.Should().Be(failed.ReceivedAt);
        stored.ProviderTimestamp.Should().Be(failed.ProviderTimestamp);
        stored.Status.Should().Be(failed.Status);
        stored.AttemptCount.Should().Be(failed.AttemptCount);
        stored.LastAttemptAt.Should().Be(failed.LastAttemptAt);
        stored.FailedAt.Should().Be(failed.FailedAt);
        stored.FailureReason.Should().Be(failed.FailureReason);
        stored.FailureCode.Should().Be(failed.FailureCode);
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentOwners_AllowsOnlyOneWinner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using (var createContext = database.CreateContext())
        {
            (await new EfCoreWebhookStore<TestContext>(createContext, clock).TryCreateAsync(CreateRecord())).Should().BeTrue();
        }

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100)
            .Select(async index =>
            {
                await start.Task;
                await using var context = database.CreateIndependentContext();
                var store = new EfCoreWebhookStore<TestContext>(context, clock);
                return await store.TryClaimAsync("webhook-1", $"worker-{index:D3}", TimeSpan.FromMinutes(5));
            })
            .ToArray();
        start.SetResult(true);

        var results = await Task.WhenAll(tasks);

        results.Count(result => result).Should().Be(1);
        await using var readContext = database.CreateIndependentContext();
        var stored = await new EfCoreWebhookStore<TestContext>(readContext, clock).GetByWebhookIdAsync("webhook-1");
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.AttemptCount.Should().Be(1);
        stored.ProcessingLeaseOwner.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TryClaimAsync_ReclaimsExpiredLease_AndStaleOwnersCannotMutateNewOwner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-old", TimeSpan.FromMinutes(1))).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));

        (await store.TryClaimAsync("webhook-1", "worker-new", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.ReleaseAsync("webhook-1", "worker-old")).Should().BeFalse();
        (await store.MarkProcessedAsync("webhook-1", "worker-old", Start.AddSeconds(30))).Should().BeFalse();
        (await store.MarkFailedAsync("webhook-1", "worker-old", Start.AddSeconds(30), "stale")).Should().BeFalse();

        var current = await store.GetByWebhookIdAsync("webhook-1");
        current!.Status.Should().Be(WebhookProcessingStatus.Processing);
        current.ProcessingLeaseOwner.Should().Be("worker-new");
        current.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task ReleaseAsync_AllowsCurrentOwnerAndMakesRecordRecoverable()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.ReleaseAsync("webhook-1", "worker-2")).Should().BeFalse();
        (await store.ReleaseAsync("webhook-1", "worker-1")).Should().BeTrue();

        var released = await store.GetByWebhookIdAsync("webhook-1");
        released!.Status.Should().Be(WebhookProcessingStatus.Received);
        released.ProcessingLeaseOwner.Should().BeNull();
        released.ProcessingLeaseExpiresAt.Should().BeNull();
        (await store.GetRecoverableAsync(Start, TimeSpan.Zero, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task MarkProcessedAsync_RequiresCurrentOwnerAndProtectsTerminalState()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkProcessedAsync("webhook-1", "worker-2", Start.AddSeconds(10))).Should().BeFalse();
        (await store.MarkProcessedAsync("webhook-1", "worker-1", Start.AddSeconds(10))).Should().BeTrue();
        (await store.MarkProcessedAsync("webhook-1", "worker-1", Start.AddSeconds(20))).Should().BeFalse();

        var processed = await store.GetByWebhookIdAsync("webhook-1");
        processed!.Status.Should().Be(WebhookProcessingStatus.Processed);
        processed.ProcessedAt.Should().Be(Start.AddSeconds(10));
        processed.ProcessingLeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_RequiresCurrentOwnerAndSanitizesFailureReason()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();

        (await store.MarkFailedAsync("webhook-1", "worker-2", Start.AddSeconds(10), "wrong-owner")).Should().BeFalse();
        (await store.MarkFailedAsync("webhook-1", "worker-1", Start.AddSeconds(10), "secret exception details")).Should().BeTrue();

        var failed = await store.GetByWebhookIdAsync("webhook-1");
        failed!.Status.Should().Be(WebhookProcessingStatus.Failed);
        failed.FailureReason.Should().Be("processing-failed");
        failed.FailureCode.Should().Be("processing-failed");
        failed.ProcessingLeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_StaleOwner_DoesNotOverwriteNewOwner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-old", TimeSpan.FromMinutes(1))).Should().BeTrue();
        var stale = await store.GetByWebhookIdAsync("webhook-1");
        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.TryClaimAsync("webhook-1", "worker-new", TimeSpan.FromMinutes(1))).Should().BeTrue();

        stale!.Status = WebhookProcessingStatus.Processed;
        stale.ProcessedAt = Start.AddSeconds(30);
        await store.UpdateAsync(stale);

        var current = await store.GetByWebhookIdAsync("webhook-1");
        current!.Status.Should().Be(WebhookProcessingStatus.Processing);
        current.ProcessingLeaseOwner.Should().Be("worker-new");
    }

    [Fact]
    public async Task UpdateAsync_PersistsGeneralRecordChanges()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();
        var record = await store.GetByWebhookIdAsync("webhook-1");

        record!.AttemptCount = 1;
        record.Status = WebhookProcessingStatus.Ignored;
        record.FailureReason = "no-handler";
        await store.UpdateAsync(record);

        var stored = await store.GetByWebhookIdAsync("webhook-1");
        stored!.AttemptCount.Should().Be(1);
        stored.Status.Should().Be(WebhookProcessingStatus.Ignored);
        stored.FailureReason.Should().Be("no-handler");
    }

    [Fact]
    public async Task GetRecoverableAsync_ExcludesTerminalAndActiveLeaseRecords()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord("received", "evt-received", Start))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("expired", "evt-expired", Start.AddSeconds(1)))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("active", "evt-active", Start.AddSeconds(2)))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("processed", "evt-processed", Start.AddSeconds(3), WebhookProcessingStatus.Processed))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("failed", "evt-failed", Start.AddSeconds(4), WebhookProcessingStatus.Failed))).Should().BeTrue();
        (await store.TryClaimAsync("expired", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.TryClaimAsync("active", "worker-1", TimeSpan.FromHours(1))).Should().BeTrue();

        var recoverable = await store.GetRecoverableAsync(Start.AddMinutes(2), TimeSpan.FromMinutes(1), 20);

        recoverable.Select(record => record.Id).Should().Equal("received", "expired");
    }

    [Fact]
    public async Task GetRecoverableAsync_IsBoundedAndOrderedByReceiptThenId()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FakeWebhookClock(Start);
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord("id-b", "evt-b", Start.AddSeconds(2)))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("id-a", "evt-a", Start.AddSeconds(2)))).Should().BeTrue();
        (await store.TryCreateAsync(CreateRecord("id-c", "evt-c", Start))).Should().BeTrue();

        var recoverable = await store.GetRecoverableAsync(Start.AddMinutes(1), TimeSpan.Zero, 2);

        recoverable.Select(record => record.Id).Should().Equal("id-c", "id-a");
    }

    [Fact]
    public async Task StoreMethods_HonorAlreadyCancelledToken()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var store = new EfCoreWebhookStore<TestContext>(context, new FakeWebhookClock(Start));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> get = async () => await store.GetAsync("stripe", "evt-1", cancellation.Token);
        Func<Task> create = async () => await store.TryCreateAsync(CreateRecord(), cancellation.Token);
        Func<Task> recover = async () => await store.GetRecoverableAsync(Start, TimeSpan.Zero, 1, cancellation.Token);

        await get.Should().ThrowAsync<OperationCanceledException>();
        await create.Should().ThrowAsync<OperationCanceledException>();
        await recover.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AddWebhookKitEntityFrameworkCore_RegistersTheEfStoreAsScoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(Start));
        services.AddDbContext<TestContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddWebhookKitEntityFrameworkCore<TestContext>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWebhookStore>().Should().BeOfType<EfCoreWebhookStore<TestContext>>();
        services.Single(descriptor => descriptor.ServiceType == typeof(IWebhookStore)).Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddWebhookKitEntityFrameworkCore_RegistersScopedStoreAndPreservesLaterCustomStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(Start));
        services.AddDbContext<TestContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddWebhookKitEntityFrameworkCore<TestContext>();
        services.AddSingleton<IWebhookStore>(new NoOpWebhookStore());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWebhookStore>().Should().BeOfType<NoOpWebhookStore>();
        services.Last(descriptor => descriptor.ServiceType == typeof(IWebhookStore)).Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task InMemoryProvider_SupportsStateTransitionsAsAConveniencePath()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new TestContext(options);
        var clock = new FakeWebhookClock(Start);
        var store = new EfCoreWebhookStore<TestContext>(context, clock);
        (await store.TryCreateAsync(CreateRecord())).Should().BeTrue();

        (await store.TryClaimAsync("webhook-1", "worker-1", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.ReleaseAsync("webhook-1", "worker-1")).Should().BeTrue();
        (await store.TryClaimAsync("webhook-1", "worker-2", TimeSpan.FromMinutes(1))).Should().BeTrue();
        (await store.MarkProcessedAsync("webhook-1", "worker-2", Start.AddSeconds(10))).Should().BeTrue();
        var entity = await context.Set<WebhookEntity>().SingleAsync();
        entity.ProcessingLeaseExpiresAtTicks.Should().BeNull();

        (await store.GetRecoverableAsync(Start, TimeSpan.Zero, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task InMemoryProvider_MapsAndReadsAWebhookRecord()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new TestContext(options);
        var store = new EfCoreWebhookStore<TestContext>(context, new FakeWebhookClock(Start));
        var record = CreateRecord("in-memory", rawBody: [4, 5, 6]);

        (await store.TryCreateAsync(record)).Should().BeTrue();
        var stored = await store.GetByWebhookIdAsync(record.Id);

        stored.Should().NotBeNull();
        stored!.RawBody.Should().Equal(4, 5, 6);
        stored.Headers["X-Test"].Should().Equal("original");
    }

    private static WebhookRecord CreateRecord(
        string id = "webhook-1",
        string eventId = "evt-1",
        DateTimeOffset? receivedAt = null,
        WebhookProcessingStatus status = WebhookProcessingStatus.Received,
        byte[]? rawBody = null)
    {
        var provider = "stripe";
        return new WebhookRecord
        {
            Id = id,
            CorrelationId = $"correlation-{id}",
            Provider = provider,
            EventId = eventId,
            DeduplicationKey = $"{provider}:{eventId}",
            EventType = "payment.succeeded",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, string[]>
            {
                ["X-Test"] = ["original"],
                ["X-Multi"] = ["one", "two"]
            },
            ContentType = "application/json",
            ContentLength = 14,
            RawBody = rawBody ?? [1, 2, 3],
            ReceivedAt = receivedAt ?? Start,
            Status = status
        };
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
        private readonly SqliteConnection _connection;

        private SqliteTestDatabase(string path, SqliteConnection connection)
        {
            _path = path;
            _connection = connection;
        }

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"webhookkit-ef-{Guid.NewGuid():N}.db");
            var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=30");
            await connection.OpenAsync();
            var database = new SqliteTestDatabase(path, connection);
            await using var context = database.CreateContext();
            await context.Database.EnsureCreatedAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
            await command.ExecuteNonQueryAsync();
            return database;
        }

        public TestContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseSqlite(_connection)
                .Options;
            return new TestContext(options);
        }

        public TestContext CreateContextWithInterceptor(IInterceptor interceptor)
        {
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseSqlite(_connection)
                .AddInterceptors(interceptor)
                .Options;
            return new TestContext(options);
        }

        public TestContext CreateIndependentContext()
        {
            var options = new DbContextOptionsBuilder<TestContext>()
                .UseSqlite($"Data Source={_path};Pooling=False;Default Timeout=30")
                .Options;
            return new TestContext(options);
        }

        public SqliteCommand CreateCommand(string commandText)
        {
            var command = _connection.CreateCommand();
            command.CommandText = commandText;
            return command;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("simulated database failure", new InvalidOperationException("not a unique violation"));
        }
    }

    private sealed class NonUniqueConstraintInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            eventData.Context!.Add(new WebhookEntity
            {
                Id = "invalid-not-null",
                Provider = "invalid",
                DeduplicationKey = "invalid:event",
                HttpMethod = "POST",
                RequestPath = "/invalid",
                HeadersJson = null!,
                ReceivedAt = Start
            });
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NoOpWebhookStore : IWebhookStore
    {
        public ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WebhookRecord?>(null);

        public ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WebhookRecord?>(null);

        public ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<WebhookRecord>>([]);
    }
}
