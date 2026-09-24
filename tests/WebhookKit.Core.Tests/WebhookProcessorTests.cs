using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Stores;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookProcessorTests
{
    [Fact]
    public void WebhookDispatchResult_PublicFactories_ExposeOnlySafeResultData()
    {
        var processed = WebhookDispatchResult.Processed();
        var ignored = WebhookDispatchResult.Ignored();
        var failed = WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed");

        processed.Status.Should().Be(WebhookDispatchStatus.Processed);
        ignored.Status.Should().Be(WebhookDispatchStatus.Ignored);
        failed.Status.Should().Be(WebhookDispatchStatus.Failed);
        failed.FailureCode.Should().Be("handler-failed");
        failed.ToString().Should().NotContain("Exception");
        var unsafeCode = () => WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "secret message");
        unsafeCode.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task DispatchAsync_WithOneHandler_ProcessesContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var context = CreateContext("event.type");

        var result = await processor.DispatchAsync(context);

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        result.FailureCode.Should().BeNull();
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("first");
    }

    [Fact]
    public async Task DispatchAsync_WithMultipleHandlers_UsesRegistrationOrder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        services.AddWebhookHandler<SecondHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("event.type"));

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("first", "second");
    }

    [Fact]
    public async Task DispatchAsync_WithDuplicateHandlerRegistration_InvokesEachDescriptor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        services.AddWebhookHandler<FirstHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("event.type"));

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("first", "first");
    }

    [Fact]
    public async Task DispatchAsync_UsesOneFreshScopeForAllHandlersInEachCall()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ScopeProbeTracker>();
        services.AddScoped<ScopeProbe>(provider =>
        {
            var tracker = provider.GetRequiredService<ScopeProbeTracker>();
            tracker.Created++;
            return new ScopeProbe(Guid.NewGuid(), tracker);
        });
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstScopedHandler>("event.type");
        services.AddWebhookHandler<SecondScopedHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var tracker = scope.ServiceProvider.GetRequiredService<ScopeProbeTracker>();

        var firstResult = await processor.DispatchAsync(CreateContext("event.type"));
        var secondResult = await processor.DispatchAsync(CreateContext("event.type"));

        firstResult.Status.Should().Be(WebhookDispatchStatus.Processed);
        secondResult.Status.Should().Be(WebhookDispatchStatus.Processed);
        tracker.Created.Should().Be(2);
        tracker.Disposed.Should().Be(2);
        tracker.ObservedScopes.Should().HaveCount(4);
        tracker.ObservedScopes[0].Should().Be(tracker.ObservedScopes[1]);
        tracker.ObservedScopes[2].Should().Be(tracker.ObservedScopes[3]);
        tracker.ObservedScopes[0].Should().NotBe(tracker.ObservedScopes[2]);
    }

    [Fact]
    public async Task DispatchAsync_WithUnregisteredEventType_ReturnsIgnoredWithoutInvokingHandlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("registered");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("unregistered"));

        result.Status.Should().Be(WebhookDispatchStatus.Ignored);
        result.FailureCode.Should().BeNull();
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WithMissingEventType_ReturnsSafePayloadFailure()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("registered");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext(null));

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureKind.Should().Be(WebhookDispatchFailureKind.Payload);
        result.FailureCode.Should().Be("missing-event-type");
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_UsesOrdinalMatchingForEventTypes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("Event.Type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var exact = await processor.DispatchAsync(CreateContext("Event.Type"));
        var differentCase = await processor.DispatchAsync(CreateContext("event.type"));

        exact.Status.Should().Be(WebhookDispatchStatus.Processed);
        differentCase.Status.Should().Be(WebhookDispatchStatus.Ignored);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("first");
    }

    [Fact]
    public async Task DispatchAsync_WhenFirstHandlerFails_ReturnsFailedAndDoesNotInvokeSecondHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FailingHandler>("event.type");
        services.AddWebhookHandler<SecondHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("event.type"));

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureKind.Should().Be(WebhookDispatchFailureKind.Handler);
        result.FailureCode.Should().Be("handler-failed");
        result.ToString().Should().NotContain("secret-handler-detail");
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("failing");
    }

    [Fact]
    public async Task DispatchAsync_WhenHandlerThrowsWebhookPayloadException_ClassifiesPayloadFailure()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit();
        services.AddWebhookHandler<PayloadFailingHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("event.type"));

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureKind.Should().Be(WebhookDispatchFailureKind.Payload);
        result.FailureCode.Should().Be("payload-invalid");
    }

    [Fact]
    public async Task DispatchAsync_WhenCancelled_PropagatesCancellationAndDoesNotReturnFailed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => processor.DispatchAsync(CreateContext("event.type"), cancellation.Token);

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenHandlerIsCancelledDuringDispatch_PropagatesCallerCancellation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CancellationProbe>();
        services.AddWebhookKit();
        services.AddWebhookHandler<CancellationHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var probe = scope.ServiceProvider.GetRequiredService<CancellationProbe>();
        using var cancellation = new CancellationTokenSource();

        var dispatch = processor.DispatchAsync(CreateContext("event.type"), cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        var act = async () => await dispatch;
        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task DispatchAsync_WhenFinalHandlerCancels_PropagatesCancellation()
    {
        var services = new ServiceCollection();
        var signal = new CancellationSignal();
        services.AddSingleton(signal);
        services.AddWebhookKit();
        services.AddWebhookHandler<FinalCancellationHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        using var cancellation = new CancellationTokenSource();
        signal.Cancel = cancellation.Cancel;

        var act = () => processor.DispatchAsync(CreateContext("event.type"), cancellation.Token);

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task DispatchAsync_WhenPayloadAccessRacesWithCancellation_RethrowsCancellationUnchanged()
    {
        var services = new ServiceCollection();
        var probe = new DeserializationCancellationProbe
        {
            CancellationException = new OperationCanceledException("payload deserialization cancelled")
        };
        var deserializer = new CancelingDeserializer(probe);
        services.AddSingleton<InvocationLog>();
        services.AddSingleton(probe);
        services.AddSingleton<IWebhookDeserializer>(deserializer);
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        scope.ServiceProvider.GetRequiredService<DeserializationCancellationProbe>().Should().BeSameAs(probe);
        using var cancellation = new CancellationTokenSource();
        var context = new WebhookContext(new byte[] { 1 }, deserializer)
        {
            WebhookId = "webhook-1",
            Provider = "test",
            EventType = "event.type",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, IReadOnlyList<string>>()
        };

        var dispatch = Task.Run(async () => await processor.DispatchAsync(context, cancellation.Token));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        probe.Release.TrySetResult(true);

        var act = async () => await dispatch;
        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.Should().BeSameAs(probe.CancellationException);
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_WhenHandlerDeserializesPayload_UsesContextPayloadBoundary()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<PayloadHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var deserializer = scope.ServiceProvider.GetRequiredService<IWebhookDeserializer>();
        var context = new WebhookContext("{\"value\":42}"u8.ToArray(), deserializer)
        {
            WebhookId = "webhook-1",
            Provider = "test",
            EventType = "event.type",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, IReadOnlyList<string>>()
        };

        var result = await processor.DispatchAsync(context);

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("payload:42");
    }

    [Fact]
    public async Task IngestionService_VerifiesExtractsDeduplicatesCreatesContextAndPersistsProcessedRecord()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-1");
        var eventType = new StubEventTypeExtractor("event.type");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-1"));

        result.Status.Should().Be(WebhookIngestionStatus.Processed);
        result.FailureCode.Should().BeNull();
        result.Record.Should().NotBeNull();
        result.Record!.EventId.Should().Be("evt-1");
        result.Record.EventType.Should().Be("event.type");
        result.Record.DeduplicationKey.Should().Be("test:evt-1");
        result.Context.Should().NotBeNull();
        log.Entries.Should().Equal("ingestion:42:evt-1");
        var stored = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync("webhook-1");
        stored!.Status.Should().Be(WebhookProcessingStatus.Processed);
    }

    [Fact]
    public async Task IngestionService_PropagatesVerifiedProviderTimestampToContextAndStoredRecord()
    {
        var providerTimestamp = new DateTimeOffset(2026, 9, 24, 0, 0, 17, TimeSpan.Zero);
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true, providerTimestamp);
        var eventId = new StubEventIdExtractor("evt-timestamp");
        var eventType = new StubEventTypeExtractor("event.type");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-timestamp"));

        result.Status.Should().Be(WebhookIngestionStatus.Processed);
        result.Record!.ProviderTimestamp.Should().Be(providerTimestamp);
        result.Context!.ProviderTimestamp.Should().Be(providerTimestamp);
        var stored = await scope.ServiceProvider.GetRequiredService<IWebhookStore>()
            .GetByWebhookIdAsync("webhook-timestamp");
        stored!.ProviderTimestamp.Should().Be(providerTimestamp);
    }

    [Theory]
    [InlineData(WebhookDispatchStatus.Processed)]
    [InlineData(WebhookDispatchStatus.Ignored)]
    [InlineData(WebhookDispatchStatus.Failed)]
    public async Task IngestionService_WhenLeaseExpiresBeforeTerminalTransition_ReturnsStatusUpdateFailureWithoutMutatingReplacementOwner(
        WebhookDispatchStatus dispatchStatus)
    {
        var clock = new FakeWebhookClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryWebhookStore(clock);
        var processor = new LeaseExpiringDispatchProcessor(store, clock, dispatchStatus);
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(clock);
        services.AddSingleton<IWebhookStore>(store);
        services.AddSingleton<IWebhookDispatchProcessor>(processor);
        services.AddSingleton<IWebhookSignatureVerifier>(new StubSignatureVerifier(isValid: true));
        services.AddSingleton<IWebhookTimestampVerifier>(new StubTimestampVerifier(isValid: true));
        services.AddSingleton<IWebhookEventIdExtractor>(new StubEventIdExtractor("evt-stale-owner"));
        services.AddSingleton<IWebhookEventTypeExtractor>(new StubEventTypeExtractor("event.type"));
        services.AddWebhookKit(options => options.AddProvider("test", provider => provider.Timestamp.AllowMissing = true));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-stale-owner"));

        processor.ReplacementClaimed.Should().BeTrue();
        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        result.FailureCode.Should().Be("status-update-failed");
        var stored = await store.GetByWebhookIdAsync("webhook-stale-owner");
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.ProcessingLeaseOwner.Should().Be(LeaseExpiringDispatchProcessor.ReplacementOwner);
        stored.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task IngestionService_WhenEventAlreadyClaimed_ReturnsDuplicateWithoutDispatch()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-duplicate");
        var eventType = new StubEventTypeExtractor("event.type");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var first = await service.IngestAsync(CreateIngestionRequest("webhook-1"));
        var second = await service.IngestAsync(CreateIngestionRequest("webhook-2"));

        first.Status.Should().Be(WebhookIngestionStatus.Processed);
        second.Status.Should().Be(WebhookIngestionStatus.Duplicate);
        log.Entries.Should().Equal("ingestion:42:evt-duplicate");
        var store = scope.ServiceProvider.GetRequiredService<IWebhookStore>();
        (await store.GetAsync("test", "evt-duplicate")).Should().NotBeNull();
        (await store.GetByWebhookIdAsync("webhook-2")).Should().BeNull();
    }

    [Fact]
    public async Task IngestionService_WhenEventTypeIsUnregistered_PersistsIgnoredWithoutDispatch()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-unregistered");
        var eventType = new StubEventTypeExtractor("unregistered");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-1"));

        result.Status.Should().Be(WebhookIngestionStatus.Ignored);
        result.Record!.Status.Should().Be(WebhookProcessingStatus.Ignored);
        log.Entries.Should().BeEmpty();
        var stored = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync("webhook-1");
        stored!.Status.Should().Be(WebhookProcessingStatus.Ignored);
    }

    [Fact]
    public async Task IngestionService_WhenEventTypeIsMissing_ReturnsPayloadFailureBeforeDeduplication()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-missing-type");
        var eventType = new StubEventTypeExtractor(null);
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-1"));

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        result.FailureKind.Should().Be(WebhookDispatchFailureKind.Payload);
        result.FailureCode.Should().Be("missing-event-type");
        log.Entries.Should().BeEmpty();
        (await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync("webhook-1")).Should().BeNull();
    }

    [Fact]
    public async Task IngestionService_WhenSignatureIsInvalid_StopsBeforeTimestampAndExtraction()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: false);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-invalid-signature");
        var eventType = new StubEventTypeExtractor("event.type");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateIngestionRequest("webhook-1"));

        result.Status.Should().Be(WebhookIngestionStatus.Rejected);
        result.FailureCode.Should().Be("signature-verification-failed");
        result.FailureReason.Should().Be("safe-signature-failure");
        signature.CallCount.Should().Be(1);
        timestamp.CallCount.Should().Be(0);
        eventId.CallCount.Should().Be(0);
        eventType.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task IngestionService_WhenCancelledBeforeVerification_PropagatesCancellation()
    {
        var log = new InvocationLog();
        var signature = new StubSignatureVerifier(isValid: true);
        var timestamp = new StubTimestampVerifier(isValid: true);
        var eventId = new StubEventIdExtractor("evt-cancelled");
        var eventType = new StubEventTypeExtractor("event.type");
        using var provider = BuildIngestionProvider(log, signature, timestamp, eventId, eventType, registerHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => service.IngestAsync(CreateIngestionRequest("webhook-1"), cancellation.Token);

        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(cancellation.Token);
        signature.CallCount.Should().Be(0);
    }

    [Fact]
    public void IngestionService_DoesNotTakeAspNetCoreInput()
    {
        var constructor = typeof(WebhookIngestionService).GetConstructors().Should().ContainSingle().Subject;
        constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.Namespace)
            .Should()
            .NotContain(namespaceName => namespaceName != null && namespaceName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchProcessorContract_ProcessesSuccessfulDispatch()
    {
        var services = new ServiceCollection();
        services.AddSingleton<InvocationLog>();
        services.AddWebhookKit();
        services.AddWebhookHandler<FirstHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();

        var result = await processor.DispatchAsync(CreateContext("event.type"));

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        scope.ServiceProvider.GetRequiredService<InvocationLog>().Entries.Should().Equal("first");
    }

    private static WebhookContext CreateContext(string? eventType)
    {
        return new WebhookContext(new byte[] { 1 }, new StringDeserializer())
        {
            WebhookId = "webhook-1",
            Provider = "test",
            EventType = eventType,
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, IReadOnlyList<string>>()
        };
    }

    public sealed class InvocationLog
    {
        public List<string> Entries { get; } = new();
    }

    public sealed class ScopeProbeTracker
    {
        public int Created { get; set; }

        public int Disposed { get; set; }

        public List<Guid> ObservedScopes { get; } = new();
    }

    public sealed class ScopeProbe(Guid id, ScopeProbeTracker tracker) : IDisposable
    {
        public Guid Id { get; } = id;

        public void Dispose()
        {
            tracker.Disposed++;
        }
    }

    public sealed class CancellationSignal
    {
        public Action? Cancel { get; set; }
    }

    public sealed class FinalCancellationHandler(CancellationSignal signal) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            signal.Cancel?.Invoke();
            return Task.CompletedTask;
        }
    }

    public sealed class DeserializationCancellationProbe
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCanceledException? CancellationException { get; init; }
    }

    public sealed class CancelingDeserializer(DeserializationCancellationProbe probe) : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult(true);
            probe.Release.Task.GetAwaiter().GetResult();
            throw probe.CancellationException ?? new OperationCanceledException();
        }
    }

    public sealed class CancellationProbe
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class CancellationHandler(CancellationProbe probe) : IWebhookHandler<string>
    {
        public async Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    public sealed class FirstHandler(InvocationLog log) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("first");
            return Task.CompletedTask;
        }
    }

    public sealed class SecondHandler(InvocationLog log) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("second");
            return Task.CompletedTask;
        }
    }

    public sealed class FirstScopedHandler(ScopeProbe probe, ScopeProbeTracker tracker) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            tracker.ObservedScopes.Add(probe.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class SecondScopedHandler(ScopeProbe probe, ScopeProbeTracker tracker) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            tracker.ObservedScopes.Add(probe.Id);
            return Task.CompletedTask;
        }
    }

    public sealed class FailingHandler(InvocationLog log) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("failing");
            throw new InvalidOperationException("secret-handler-detail");
        }
    }

    public sealed class PayloadFailingHandler : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            throw new WebhookKit.Abstractions.Exceptions.WebhookPayloadException();
        }
    }

    public sealed class PayloadHandler(InvocationLog log) : IWebhookHandler<ProcessorPayload>
    {
        public Task HandleAsync(ProcessorPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            log.Entries.Add($"payload:{context.GetPayload<ProcessorPayload>().Value}");
            return Task.CompletedTask;
        }
    }

    public sealed record ProcessorPayload(int Value);

    private static ServiceProvider BuildIngestionProvider(
        InvocationLog log,
        StubSignatureVerifier signature,
        StubTimestampVerifier timestamp,
        StubEventIdExtractor eventId,
        StubEventTypeExtractor eventType,
        bool registerHandler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddSingleton<IWebhookSignatureVerifier>(signature);
        services.AddSingleton<IWebhookTimestampVerifier>(timestamp);
        services.AddSingleton<IWebhookEventIdExtractor>(eventId);
        services.AddSingleton<IWebhookEventTypeExtractor>(eventType);
        services.AddWebhookKit(options => options.AddProvider("test", provider =>
        {
            provider.AllowBodyHashFallback = true;
            provider.Timestamp.AllowMissing = true;
        }));
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero)));
        if (registerHandler)
        {
            services.AddWebhookHandler<IngestionHandler>("event.type");
        }

        return services.BuildServiceProvider();
    }

    private static WebhookIngestionRequest CreateIngestionRequest(string webhookId)
    {
        return new WebhookIngestionRequest
        {
            WebhookId = webhookId,
            Provider = "test",
            HttpMethod = "POST",
            RequestPath = "/webhooks/test",
            Headers = new Dictionary<string, IReadOnlyList<string>> { ["X-Test"] = ["value"] },
            RawBody = "{\"value\":42}"u8.ToArray(),
            ContentType = "application/json",
            ContentLength = 12
        };
    }

    public sealed class StubSignatureVerifier(bool isValid) : IWebhookSignatureVerifier
    {
        public int CallCount { get; private set; }

        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(isValid
                ? WebhookVerificationResult.Success()
                : WebhookVerificationResult.Fail("safe-signature-failure"));
        }
    }

    public sealed class StubTimestampVerifier(bool isValid, DateTimeOffset? providerTimestamp = null) : IWebhookTimestampVerifier
    {
        public int CallCount { get; private set; }

        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new WebhookVerificationResult(
                isValid,
                isValid ? null : "safe-timestamp-failure",
                providerTimestamp));
        }
    }

    public sealed class StubEventIdExtractor(string? value) : IWebhookEventIdExtractor
    {
        public int CallCount { get; private set; }

        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(value);
        }
    }

    public sealed class StubEventTypeExtractor(string? value) : IWebhookEventTypeExtractor
    {
        public int CallCount { get; private set; }

        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(value);
        }
    }

    private sealed class LeaseExpiringDispatchProcessor(
        IWebhookStore store,
        FakeWebhookClock clock,
        WebhookDispatchStatus status) : IWebhookDispatchProcessor
    {
        public const string ReplacementOwner = "replacement-worker";
        public bool ReplacementClaimed { get; private set; }

        public async Task<WebhookDispatchResult> DispatchAsync(
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            ReplacementClaimed = await store.TryClaimAsync(
                context.WebhookId,
                ReplacementOwner,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            return status switch
            {
                WebhookDispatchStatus.Processed => WebhookDispatchResult.Processed(),
                WebhookDispatchStatus.Ignored => WebhookDispatchResult.Ignored(),
                _ => WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed")
            };
        }
    }

    public sealed class IngestionHandler(InvocationLog log) : IWebhookHandler<IngestionPayload>
    {
        public Task HandleAsync(IngestionPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            log.Entries.Add($"ingestion:{eventData.Value}:{context.EventId}");
            return Task.CompletedTask;
        }
    }

    public sealed record IngestionPayload(int Value);

    private sealed class StringDeserializer : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            return (T)(object)"test";
        }
    }
}
