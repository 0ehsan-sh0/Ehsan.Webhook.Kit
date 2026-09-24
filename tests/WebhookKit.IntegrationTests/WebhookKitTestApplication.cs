using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Queues;
using WebhookKit.Core.Retries;
using WebhookKit.Core.Options;
using WebhookKit.Core.Stores;
using WebhookKit.Testing;

namespace WebhookKit.IntegrationTests;

public enum WebhookKitHandlerBehavior
{
    Succeed,
    FailThenSucceed,
    AlwaysFail
}

public sealed class WebhookKitTestApplicationOptions
{
    public bool EnableBackgroundWorker { get; init; }
    public bool RegisterKnownHandler { get; init; } = true;
    public bool RegisterCustomHandler { get; init; }
    public bool UseCustomExtractors { get; init; }
    public bool UseControlledRetryDelay { get; init; }
    public WebhookKitHandlerBehavior HandlerBehavior { get; init; }
    public int FailuresBeforeSuccess { get; init; } = 2;
    public long MaxRequestBodySizeBytes { get; init; } = 1024;
}

public sealed record IntegrationPayload(int Value);

public sealed record HandlerCall(IntegrationPayload Payload, WebhookContext Context, int Attempt);

public sealed record ActionCall(IntegrationPayload Payload, WebhookContext Context);

public sealed class HandlerProbe
{
    private readonly ConcurrentQueue<HandlerCall> _calls = new();
    private readonly TaskCompletionSource<HandlerCall> _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _invocations;
    private int _successes;
    private int _failuresBeforeSuccess;
    private WebhookKitHandlerBehavior _behavior;

    public IReadOnlyList<HandlerCall> Calls => _calls.ToArray();

    public int Invocations => Volatile.Read(ref _invocations);

    public int Successes => Volatile.Read(ref _successes);

    public Task<HandlerCall> WaitForFirstAsync(TimeSpan timeout)
    {
        return _firstCall.Task.WaitAsync(timeout);
    }

    public void Configure(WebhookKitHandlerBehavior behavior, int failuresBeforeSuccess)
    {
        _behavior = behavior;
        _failuresBeforeSuccess = Math.Max(0, failuresBeforeSuccess);
    }

    public Task HandleAsync(IntegrationPayload payload, WebhookContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = Interlocked.Increment(ref _invocations);
        var call = new HandlerCall(payload, context, attempt);
        _calls.Enqueue(call);
        _firstCall.TrySetResult(call);
        if (_behavior == WebhookKitHandlerBehavior.AlwaysFail ||
            (_behavior == WebhookKitHandlerBehavior.FailThenSucceed && attempt <= _failuresBeforeSuccess))
        {
            throw new WebhookRetryableException("sensitive integration retry detail");
        }

        Interlocked.Increment(ref _successes);
        return Task.CompletedTask;
    }
}

public sealed class CustomHandlerProbe
{
    private readonly ConcurrentQueue<HandlerCall> _calls = new();
    private readonly TaskCompletionSource<HandlerCall> _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _invocations;
    private int _successes;

    public IReadOnlyList<HandlerCall> Calls => _calls.ToArray();

    public int Invocations => Volatile.Read(ref _invocations);

    public int Successes => Volatile.Read(ref _successes);

    public Task<HandlerCall> WaitForFirstAsync(TimeSpan timeout)
    {
        return _firstCall.Task.WaitAsync(timeout);
    }

    public Task HandleAsync(IntegrationPayload payload, WebhookContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _invocations);
        var call = new HandlerCall(payload, context, Invocations);
        _calls.Enqueue(call);
        _firstCall.TrySetResult(call);
        Interlocked.Increment(ref _successes);
        return Task.CompletedTask;
    }
}

public sealed class ActionProbe
{
    private readonly ConcurrentQueue<ActionCall> _calls = new();
    private int _invocations;

    public IReadOnlyList<ActionCall> Calls => _calls.ToArray();

    public int Invocations => Volatile.Read(ref _invocations);

    public void Record(IntegrationPayload payload, WebhookContext context)
    {
        Interlocked.Increment(ref _invocations);
        _calls.Enqueue(new ActionCall(payload, context));
    }
}

public sealed class CustomExtractorProbe
{
    private int _eventIdCalls;
    private int _eventTypeCalls;

    public int EventIdCalls => Volatile.Read(ref _eventIdCalls);

    public int EventTypeCalls => Volatile.Read(ref _eventTypeCalls);

    public void RecordEventId()
    {
        Interlocked.Increment(ref _eventIdCalls);
    }

    public void RecordEventType()
    {
        Interlocked.Increment(ref _eventTypeCalls);
    }
}

public sealed class QueueProbe
{
    private int _enqueuedCount;
    private int _dequeuedCount;
    private int _completedCount;

    public int EnqueuedCount => Volatile.Read(ref _enqueuedCount);

    public int DequeuedCount => Volatile.Read(ref _dequeuedCount);

    public int CompletedCount => Volatile.Read(ref _completedCount);

    public void RecordEnqueue()
    {
        Interlocked.Increment(ref _enqueuedCount);
    }

    public void RecordDequeue()
    {
        Interlocked.Increment(ref _dequeuedCount);
    }

    public void RecordComplete()
    {
        Interlocked.Increment(ref _completedCount);
    }
}

public sealed class RetryDelayProbe
{
    private readonly ConcurrentQueue<TimeSpan> _delays = new();

    public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

    public void Record(TimeSpan delay)
    {
        _delays.Enqueue(delay);
    }
}

public sealed class ImmediateRetryDelay(RetryDelayProbe probe) : IWebhookRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.Record(delay);
        return Task.CompletedTask;
    }
}

public sealed class ControlledRetryDelay(RetryDelayProbe probe) : IWebhookRetryDelay
{
    private readonly Channel<RetryDelayEntry> _entries = Channel.CreateUnbounded<RetryDelayEntry>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public IReadOnlyList<TimeSpan> Delays => probe.Delays;

    public async Task<RetryDelayEntry> WaitForEntryAsync(TimeSpan timeout)
    {
        return await _entries.Reader.ReadAsync().AsTask().WaitAsync(timeout);
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = new RetryDelayEntry(delay);
        probe.Record(delay);
        _entries.Writer.TryWrite(entry);
        await entry.WaitAsync(cancellationToken);
    }
}

public sealed class RetryDelayEntry(TimeSpan delay)
{
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan Delay { get; } = delay;

    public void Release()
    {
        _release.TrySetResult(true);
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        return _release.Task.WaitAsync(cancellationToken);
    }
}

public sealed class StoreProbe
{
    private readonly ConcurrentQueue<string> _createdIds = new();
    private readonly TaskCompletionSource<WebhookRecord> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _createdCount;

    public int CreatedCount => Volatile.Read(ref _createdCount);

    public IReadOnlyList<string> CreatedIds => _createdIds.ToArray();

    public Task<WebhookRecord> WaitForTerminalAsync(TimeSpan timeout)
    {
        return _terminal.Task.WaitAsync(timeout);
    }

    public void RecordCreated(string webhookId)
    {
        Interlocked.Increment(ref _createdCount);
        _createdIds.Enqueue(webhookId);
    }

    public async ValueTask SignalTerminalAsync(IWebhookStore store, string webhookId, CancellationToken cancellationToken)
    {
        var record = await store.GetByWebhookIdAsync(webhookId, cancellationToken);
        if (record is not null && record.Status is WebhookProcessingStatus.Processed or WebhookProcessingStatus.Failed or WebhookProcessingStatus.Ignored)
        {
            _terminal.TrySetResult(record);
        }
    }
}

public sealed class ProbedWebhookStore(IWebhookStore inner, StoreProbe probe) : IWebhookStore
{
    public ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default)
    {
        return inner.GetAsync(provider, eventId, cancellationToken);
    }

    public async ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        var created = await inner.TryCreateAsync(record, cancellationToken);
        if (created)
        {
            probe.RecordCreated(record.Id);
        }

        return created;
    }

    public async ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        await inner.UpdateAsync(record, cancellationToken);
        if (record.Status is WebhookProcessingStatus.Processed or WebhookProcessingStatus.Failed or WebhookProcessingStatus.Ignored)
        {
            await probe.SignalTerminalAsync(inner, record.Id, cancellationToken);
        }
    }

    public ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default)
    {
        return inner.GetByWebhookIdAsync(webhookId, cancellationToken);
    }

    public ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        return inner.TryClaimAsync(webhookId, leaseOwner, leaseDuration, cancellationToken);
    }

    public ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default)
    {
        return inner.ReleaseAsync(webhookId, leaseOwner, cancellationToken);
    }

    public async ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default)
    {
        var marked = await inner.MarkProcessedAsync(webhookId, leaseOwner, processedAt, cancellationToken);
        if (marked)
        {
            await probe.SignalTerminalAsync(inner, webhookId, cancellationToken);
        }

        return marked;
    }

    public async ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default)
    {
        var marked = await inner.MarkFailedAsync(webhookId, leaseOwner, failedAt, failureReason, cancellationToken);
        if (marked)
        {
            await probe.SignalTerminalAsync(inner, webhookId, cancellationToken);
        }

        return marked;
    }

    public ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default)
    {
        return inner.GetRecoverableAsync(now, expiredLeaseAge, limit, cancellationToken);
    }
}

public sealed class QueueDecorator(IWebhookQueue inner, QueueProbe probe) : IWebhookQueue
{
    public async ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
    {
        var enqueued = await inner.TryEnqueueAsync(workItem, cancellationToken);
        if (enqueued)
        {
            probe.RecordEnqueue();
        }

        return enqueued;
    }

    public async ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var workItem = await inner.DequeueAsync(cancellationToken);
        probe.RecordDequeue();
        return workItem;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        await inner.CompleteAsync(cancellationToken);
        probe.RecordComplete();
    }
}

public sealed class NestedEventIdExtractor(CustomExtractorProbe probe) : IWebhookEventIdExtractor
{
    public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.RecordEventId();
        return ValueTask.FromResult(NestedJson.Read(context.RawBody, "data", "id"));
    }
}

public sealed class NestedEventTypeExtractor(CustomExtractorProbe probe) : IWebhookEventTypeExtractor
{
    public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.RecordEventType();
        return ValueTask.FromResult(NestedJson.Read(context.RawBody, "data", "type"));
    }
}

public static class NestedJson
{
    public static string? Read(ReadOnlyMemory<byte> rawBody, params string[] path)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var current = document.RootElement;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                current = next;
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class IntegrationHandler(HandlerProbe probe) : IWebhookHandler<IntegrationPayload>
{
    public Task HandleAsync(IntegrationPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
    {
        return probe.HandleAsync(eventData, context, cancellationToken);
    }
}

public sealed class CustomIntegrationHandler(CustomHandlerProbe probe) : IWebhookHandler<IntegrationPayload>
{
    public Task HandleAsync(IntegrationPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
    {
        return probe.HandleAsync(eventData, context, cancellationToken);
    }
}

[ApiController]
[Route("mvc")]
public sealed class WebhookScenariosController(ActionProbe probe) : ControllerBase
{
    [HttpPost("valid")]
    [WebhookEndpoint(WebhookKitTestApplication.ProviderName)]
    public IActionResult Valid(WebhookContext context)
    {
        var payload = context.GetPayload<IntegrationPayload>();
        probe.Record(payload, context);
        return Ok();
    }
}

public sealed class WebhookKitTestApplication : IAsyncDisposable, IDisposable
{
    public const string ProviderName = "integration-provider";
    public const string KnownEventType = "known.event";
    public const string CustomEventType = "custom.event";
    public const string Secret = "integration-safe-secret";
    public const string TraceId = "integration-trace";
    public const string MinimalPath = "/webhooks/minimal";
    public const string AsyncPath = "/webhooks/async";
    public const string CustomPath = "/webhooks/custom";
    public static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private readonly WebhookKitTestApplicationOptions _options;
    private readonly TestServer _server;
    private readonly WebhookSignatureGenerator _signatureGenerator;
    private int _started;
    private int _disposed;
    private HttpClient? _client;

    public WebhookKitTestApplication(WebhookKitTestApplicationOptions? options = null)
    {
        _options = options ?? new WebhookKitTestApplicationOptions();
        Clock = new FakeWebhookClock(FixedNow);
        HandlerProbe = new HandlerProbe();
        HandlerProbe.Configure(_options.HandlerBehavior, _options.FailuresBeforeSuccess);
        CustomHandlerProbe = new CustomHandlerProbe();
        ActionProbe = new ActionProbe();
        CustomExtractorProbe = new CustomExtractorProbe();
        StoreProbe = new StoreProbe();
        QueueProbe = new QueueProbe();
        RetryDelayProbe = new RetryDelayProbe();
        var underlyingStore = new InMemoryWebhookStore(Clock);
        Store = new ProbedWebhookStore(underlyingStore, StoreProbe);
        var queueOptions = new WebhookKitOptions
        {
            Queue = new WebhookQueueOptions { Capacity = 8 }
        };
        Queue = new QueueDecorator(new ChannelWebhookQueue(Options.Create(queueOptions)), QueueProbe);
        RetryDelay = _options.UseControlledRetryDelay
            ? new ControlledRetryDelay(RetryDelayProbe)
            : new ImmediateRetryDelay(RetryDelayProbe);
        _signatureGenerator = new WebhookSignatureGenerator(Secret);
        var builder = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.TraceIdentifier = TraceId;
                    await next();
                });
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapWebhook(MinimalPath, ProviderName);
                    endpoints.MapWebhook(AsyncPath, new WebhookEndpointOptions(ProviderName)
                    {
                        Mode = WebhookProcessingMode.Asynchronous
                    });
                    endpoints.MapWebhook(CustomPath, ProviderName);
                });
            });
        _server = new TestServer(builder);
    }

    public FakeWebhookClock Clock { get; }

    public HandlerProbe HandlerProbe { get; }

    public CustomHandlerProbe CustomHandlerProbe { get; }

    public ActionProbe ActionProbe { get; }

    public CustomExtractorProbe CustomExtractorProbe { get; }

    public StoreProbe StoreProbe { get; }

    public QueueProbe QueueProbe { get; }

    public RetryDelayProbe RetryDelayProbe { get; }

    public IWebhookStore Store { get; }

    public IWebhookQueue Queue { get; }

    public IWebhookRetryDelay RetryDelay { get; }

    public int RecordCount => StoreProbe.CreatedCount;

    public HttpClient Client => _client ?? throw new InvalidOperationException("The test application has not been started.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _server.Host.StartAsync(cancellationToken);
            _client = _server.CreateClient();
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    public async Task<HttpResponseMessage> SendSignedAsync(
        string path,
        string eventId,
        string eventType,
        string body = "{\"value\":42}",
        DateTimeOffset? timestamp = null,
        string? signature = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateSignedRequest(path, eventId, eventType, body, timestamp, signature);
        return await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public async Task<HttpResponseMessage> SendSignedWithoutMetadataAsync(
        string path,
        string body = "{\"value\":42}",
        DateTimeOffset? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateSignedRequest(path, null, null, body, timestamp, null);
        return await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        return Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public Task<HandlerCall> WaitForHandlerAsync()
    {
        return HandlerProbe.WaitForFirstAsync(TestTimeout);
    }

    public Task<WebhookRecord> WaitForTerminalAsync()
    {
        return StoreProbe.WaitForTerminalAsync(TestTimeout);
    }

    public async Task<RetryDelayEntry> WaitForRetryDelayAsync()
    {
        if (RetryDelay is not ControlledRetryDelay controlled)
        {
            throw new InvalidOperationException("The current application does not use a controlled retry delay.");
        }

        return await controlled.WaitForEntryAsync(TestTimeout);
    }

    public ValueTask<WebhookRecord?> GetRecordAsync(string eventId, CancellationToken cancellationToken = default)
    {
        return Store.GetAsync(ProviderName, eventId, cancellationToken);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        if (Volatile.Read(ref _started) != 0)
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            try
            {
                await _server.Host.StopAsync(cancellation.Token);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        try
        {
            _client?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            _server.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddRouting();
        services.AddControllers().AddApplicationPart(typeof(WebhookScenariosController).Assembly);
        services.AddWebhookKit(options =>
        {
            options.MaxRequestBodySizeBytes = _options.MaxRequestBodySizeBytes;
            options.Storage.PersistRawBody = true;
            options.Queue.Capacity = 8;
            options.Background.Enabled = _options.EnableBackgroundWorker;
            options.Background.WorkerConcurrency = 1;
            options.Background.RecoveryInterval = TimeSpan.FromHours(1);
            options.Background.RecoveryBatchSize = 8;
            options.Background.LeaseDuration = TimeSpan.FromMinutes(2);
            options.Background.RecoveryAge = TimeSpan.FromMinutes(1);
            options.AddProvider(ProviderName, provider =>
            {
                provider.Signature.HeaderName = WebhookTestRequestBuilder.DefaultSignatureHeaderName;
                provider.Signature.Secret = Secret;
                provider.Timestamp.HeaderName = WebhookTestRequestBuilder.DefaultTimestampHeaderName;
                provider.EventIdHeaderName = WebhookTestRequestBuilder.DefaultEventIdHeaderName;
                provider.EventTypeHeaderName = WebhookTestRequestBuilder.DefaultEventTypeHeaderName;
                provider.Retry.MaxAttempts = 3;
                provider.Retry.InitialDelay = TimeSpan.FromSeconds(2);
                provider.Retry.BackoffMultiplier = 2;
                provider.Retry.UseJitter = false;
            });
        });
        services.RemoveAll<IWebhookClock>();
        services.AddSingleton<IWebhookClock>(Clock);
        services.RemoveAll<IWebhookStore>();
        services.AddSingleton<IWebhookStore>(Store);
        services.RemoveAll<IWebhookQueue>();
        services.AddSingleton<IWebhookQueue>(Queue);
        services.AddSingleton(HandlerProbe);
        services.AddSingleton(CustomHandlerProbe);
        services.AddSingleton(ActionProbe);
        services.AddSingleton(CustomExtractorProbe);
        services.AddSingleton<IWebhookRetryDelay>(RetryDelay);
        services.AddWebhookKitAspNetCore();
        if (_options.RegisterKnownHandler)
        {
            services.AddWebhookHandler<IntegrationHandler>(KnownEventType);
        }

        if (_options.RegisterCustomHandler)
        {
            services.AddWebhookHandler<CustomIntegrationHandler>(CustomEventType);
        }

        if (_options.UseCustomExtractors)
        {
            services.RemoveAll<IWebhookEventIdExtractor>();
            services.RemoveAll<IWebhookEventTypeExtractor>();
            services.AddSingleton<IWebhookEventIdExtractor>(sp => new NestedEventIdExtractor(sp.GetRequiredService<CustomExtractorProbe>()));
            services.AddSingleton<IWebhookEventTypeExtractor>(sp => new NestedEventTypeExtractor(sp.GetRequiredService<CustomExtractorProbe>()));
        }
    }

    private HttpRequestMessage CreateSignedRequest(
        string path,
        string? eventId,
        string? eventType,
        string body,
        DateTimeOffset? timestamp,
        string? signature)
    {
        var builder = WebhookTestRequestBuilder.Create(ProviderName, eventId, eventType, Clock)
            .WithPath(path)
            .WithRawBody(Encoding.UTF8.GetBytes(body))
            .WithContentType("application/json")
            .WithTimestamp(timestamp ?? FixedNow);
        var request = builder.BuildSigned(_signatureGenerator);
        if (signature is not null)
        {
            request.Headers.Remove(WebhookTestRequestBuilder.DefaultSignatureHeaderName);
            request.Headers.TryAddWithoutValidation(WebhookTestRequestBuilder.DefaultSignatureHeaderName, signature);
        }

        return request;
    }
}
