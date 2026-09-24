using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.IntegrationTests;

public sealed class ConcurrencyWebhookTests
{
    private const string Provider = "concurrency-integration";
    private const string EventId = "evt-concurrency-integration";
    private const string EventType = "concurrency.event";
    private const string Secret = "concurrency-integration-secret";
    private const int DuplicateStatusCode = 208;
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task TestServerConcurrencySignedRequestsProcessesOneAndAcknowledgesDuplicates(int level)
    {
        var clock = new FakeWebhookClock(Start);
        var idGenerator = new RecordingWebhookIdGenerator(new WebhookIdGenerator(clock));
        var storeProbe = new StoreProbe();
        var handlerProbe = new HandlerProbe();
        var underlyingStore = new WebhookKit.Core.Stores.InMemoryWebhookStore(clock);
        var store = new CountingWebhookStore(underlyingStore, storeProbe);
        var builder = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((_, services) =>
            {
                services.AddRouting();
                services.AddWebhookKit(ConfigureProvider);
                services.AddSingleton<IWebhookClock>(clock);
                services.AddSingleton<IWebhookIdGenerator>(idGenerator);
                services.AddSingleton<IWebhookStore>(store);
                services.AddSingleton(handlerProbe);
                services.AddWebhookKitAspNetCore();
                services.AddWebhookHandler<CountingHandler>(EventType);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapWebhook("/concurrency", new WebhookEndpointOptions(Provider)
                    {
                        Response = new WebhookEndpointResponseOptions
                        {
                            ProcessedStatusCode = StatusCodes.Status200OK,
                            DuplicateStatusCode = DuplicateStatusCode
                        }
                    });
                });
            });

        using var server = new TestServer(builder);
        using var client = server.CreateClient();
        var requestBuilder = WebhookTestRequestBuilder
            .Create(Provider, EventId, EventType, clock)
            .WithPath("/concurrency")
            .WithTimestamp(Start)
            .WithRawBody(BodyBytes())
            .WithContentType("application/json");
        var signatureGenerator = new WebhookSignatureGenerator(Secret);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tasks = Enumerable.Range(0, level).Select(async _ =>
        {
            await gate.Task.WaitAsync(TestTimeout);
            using var request = requestBuilder.BuildSigned(signatureGenerator);
            using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation.Token)
                .WaitAsync(TestTimeout);
            return response.StatusCode;
        }).ToArray();

        gate.SetResult(true);
        var statuses = await Task.WhenAll(tasks).WaitAsync(TestTimeout);
        var ids = idGenerator.Ids.ToArray();

        statuses.Should().HaveCount(level);
        statuses.Count(status => status == HttpStatusCode.OK).Should().Be(1);
        statuses.Count(status => (int)status == DuplicateStatusCode).Should().Be(level - 1);
        storeProbe.DeduplicationClaims.Should().Be(1);
        storeProbe.ProcessingClaims.Should().Be(1);
        handlerProbe.Executions.Should().Be(1);
        ids.Should().HaveCount(level);
        ids.Distinct(StringComparer.Ordinal).Should().HaveCount(level);
        foreach (var id in ids)
        {
            IsCanonicalUlid(id).Should().BeTrue();
        }

        var records = await Task.WhenAll(ids.Select(id =>
                underlyingStore.GetByWebhookIdAsync(id).AsTask().WaitAsync(TestTimeout)))
            .WaitAsync(TestTimeout);
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
            provider.Signature.HeaderName = WebhookTestRequestBuilder.DefaultSignatureHeaderName;
            provider.Signature.Secret = Secret;
            provider.Timestamp.HeaderName = WebhookTestRequestBuilder.DefaultTimestampHeaderName;
            provider.EventIdHeaderName = WebhookTestRequestBuilder.DefaultEventIdHeaderName;
            provider.EventTypeHeaderName = WebhookTestRequestBuilder.DefaultEventTypeHeaderName;
        });
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

    private sealed class RecordingWebhookIdGenerator(IWebhookIdGenerator inner) : IWebhookIdGenerator
    {
        private readonly ConcurrentQueue<string> _ids = new();

        public IReadOnlyCollection<string> Ids => _ids.ToArray();

        public string Create()
        {
            var id = inner.Create();
            _ids.Enqueue(id);
            return id;
        }
    }

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
