using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Retries;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

public sealed class WebhookEndpointServiceTests
{
    private const string ProviderName = "endpoint-provider";
    private const string SignatureHeader = "X-Signature";
    private const string TimestampHeader = "X-Timestamp";
    private const string EventIdHeader = "X-Event-Id";
    private const string EventTypeHeader = "X-Event-Type";
    private const string Secret = "endpoint-secret-value";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProcessAsync_ValidSynchronousRequest_ProcessesAndPersistsTerminalRecord()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-1", "known.event");
        var handler = new RecordingHandler();
        using var provider = BuildProvider(handler: handler);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Code.Should().Be("processed");
        result.Context.Should().NotBeNull();
        result.Context!.EventId.Should().Be("evt-1");
        result.Context.EventType.Should().Be("known.event");
        result.Context.Headers[EventIdHeader].Should().Equal("evt-1");
        handler.Calls.Should().Be(1);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync(result.Context.WebhookId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(WebhookProcessingStatus.Processed);
    }

    [Fact]
    public async Task ProcessAsync_InvalidSignature_ReturnsFixedUnauthorizedResultWithoutVerifierReason()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-invalid", "known.event", signature: "not-a-valid-signature");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.InvalidSignature);
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Code.Should().Be("signature-verification-failed");
        result.Message.Should().NotContain("not-a-valid-signature");
        result.ToString().Should().NotContain(Secret);
        result.ToString().Should().NotContain("Signature mismatch");
    }

    [Fact]
    public async Task ProcessAsync_ExpiredTimestamp_ReturnsBadRequestWithoutDisclosingReason()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-expired", "known.event", timestamp: FixedNow.AddMinutes(-6));
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.InvalidTimestamp);
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("timestamp-verification-failed");
        result.Message.Should().NotContain("seconds");
    }

    [Fact]
    public async Task ProcessAsync_MissingTimestamp_ReturnsBadRequestWhenProtectionIsNotOptedOut()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-missing-timestamp", "known.event", includeTimestamp: false);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.InvalidTimestamp);
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void ProcessAsync_MissingTimestampConfiguration_FailsValidation()
    {
        var calls = new List<string>();
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-no-timestamp-config", "known.event");
        var reader = new RecordingBodyReader(calls, body);
        using var provider = BuildProvider(
            bodyReader: reader,
            configureProvider: provider => provider.Timestamp.HeaderName = null);
        using var scope = provider.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        act.Should().Throw<OptionsValidationException>();
        reader.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_MissingEventId_ReturnsBadRequest()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, eventId: null, eventType: "known.event");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.MissingEventId);
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("event-id-required");
    }

    [Fact]
    public async Task ProcessAsync_MissingEventType_ReturnsBadRequest()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-missing-type", eventType: null);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.MissingEventType);
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("missing-event-type");
    }

    [Fact]
    public async Task ProcessAsync_UnknownEventType_IsAcknowledgedAndPersistedAsIgnored()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-unknown", "unknown.event");
        var handler = new RecordingHandler();
        using var provider = BuildProvider(handler: handler);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Ignored);
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.Code.Should().Be("ignored");
        handler.Calls.Should().Be(0);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-unknown");
        record!.Status.Should().Be(WebhookProcessingStatus.Ignored);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateRequest_ReturnsSuccessWithoutDispatchingAgain()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var firstContext = CreateRequest(body, "evt-duplicate", "known.event");
        var secondContext = CreateRequest(body, "evt-duplicate", "known.event");
        var handler = new RecordingHandler();
        using var provider = BuildProvider(handler: handler);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var first = await service.ProcessAsync(firstContext, CreateEndpointOptions());
        var second = await service.ProcessAsync(secondContext, CreateEndpointOptions());

        first.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        second.Outcome.Should().Be(WebhookEndpointOutcome.Duplicate);
        second.StatusCode.Should().Be(StatusCodes.Status200OK);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_OversizedBody_ReturnsPayloadTooLarge()
    {
        var body = new byte[32];
        var context = CreateRequest(body, "evt-large", "known.event");
        using var provider = BuildProvider(providerBodyLimit: 8);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.PayloadTooLarge);
        result.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        result.Code.Should().Be("payload-too-large");
    }

    [Fact]
    public async Task ProcessAsync_CancelledToken_PropagatesCancellation()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-cancelled", "known.event");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var act = () => service.ProcessAsync(context, CreateEndpointOptions(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProcessAsync_AsynchronousAdmission_ReturnsAcceptedAndQueuesOnlyReference()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-async", "known.event");
        var queue = new RecordingQueue();
        using var provider = BuildProvider(queue: queue);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions(WebhookProcessingMode.Asynchronous));

        result.Outcome.Should().Be(WebhookEndpointOutcome.Accepted);
        result.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        queue.Items.Should().ContainSingle();
        queue.Items[0].WebhookId.Should().Be(result.Context!.WebhookId);
        queue.Items[0].Provider.Should().Be(ProviderName);
        typeof(WebhookWorkItem).GetProperties().Select(property => property.Name).Should().NotContain("RawBody");
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync(queue.Items[0].WebhookId);
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
        record.RawBody.Should().Equal(body);
    }

    [Fact]
    public async Task ProcessAsync_AsynchronousDuplicate_ReturnsAcceptedWithoutReenqueue()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var queue = new RecordingQueue();
        using var provider = BuildProvider(queue: queue);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var options = CreateEndpointOptions(WebhookProcessingMode.Asynchronous);

        var first = await service.ProcessAsync(CreateRequest(body, "evt-async-duplicate", "known.event"), options);
        var second = await service.ProcessAsync(CreateRequest(body, "evt-async-duplicate", "known.event"), options);

        first.Outcome.Should().Be(WebhookEndpointOutcome.Accepted);
        second.Outcome.Should().Be(WebhookEndpointOutcome.Duplicate);
        second.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        queue.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ProcessAsync_AsynchronousAdmissionWithoutQueue_ReturnsServiceUnavailableAndLeavesRecordRecoverable()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-no-queue", "known.event");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions(WebhookProcessingMode.Asynchronous));

        result.Outcome.Should().Be(WebhookEndpointOutcome.QueueUnavailable);
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        result.Code.Should().Be("queue-unavailable");
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-no-queue");
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
    }

    [Fact]
    public async Task ProcessAsync_AsynchronousAdmissionWithFullQueue_ReturnsServiceUnavailableAndLeavesRecordRecoverable()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-full-queue", "known.event");
        var queue = new RecordingQueue { EnqueueResult = false };
        using var provider = BuildProvider(queue: queue);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions(WebhookProcessingMode.Asynchronous));

        result.Outcome.Should().Be(WebhookEndpointOutcome.QueueUnavailable);
        result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-full-queue");
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
    }

    [Fact]
    public async Task ProcessAsync_AsynchronousAdmissionWithRawBodyPersistenceDisabled_FailsFast()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-no-storage", "known.event");
        using var provider = BuildProvider(configureStorage: storage => storage.PersistRawBody = false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var act = () => service.ProcessAsync(context, CreateEndpointOptions(WebhookProcessingMode.Asynchronous));

        await act.Should().ThrowAsync<WebhookConfigurationException>();
        (await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-no-storage")).Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_VerificationPrecedesExtractionAndDoesNotDeserializeBeforeVerification()
    {
        var calls = new List<string>();
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-order", "known.event", includeSignature: false);
        using var provider = BuildProvider(
            signatureVerifier: new RecordingSignatureVerifier(calls),
            timestampVerifier: new RecordingTimestampVerifier(calls),
            eventIdExtractor: new RecordingEventIdExtractor(calls, "evt-order"),
            eventTypeExtractor: new RecordingEventTypeExtractor(calls, "known.event"),
            deserializer: new RecordingDeserializer(calls),
            registerHandler: false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Ignored);
        calls.Should().Equal("signature", "timestamp", "event-id", "event-type");
        calls.Should().NotContain("deserialize");
    }

    [Fact]
    public async Task ProcessAsync_UsesGlobalLimitWhenProviderHasNoOverride()
    {
        var calls = new List<string>();
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-global-limit", "known.event", includeSignature: false);
        var reader = new RecordingBodyReader(calls, body);
        using var provider = BuildProvider(
            providerBodyLimit: null,
            bodyReader: reader,
            signatureVerifier: new RecordingSignatureVerifier(calls),
            timestampVerifier: new RecordingTimestampVerifier(calls),
            eventIdExtractor: new RecordingEventIdExtractor(calls, "evt-global-limit"),
            eventTypeExtractor: new RecordingEventTypeExtractor(calls, "known.event"),
            registerHandler: false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        await service.ProcessAsync(context, CreateEndpointOptions());

        reader.MaxSizes.Should().Equal(1024L);
    }

    [Fact]
    public async Task ProcessAsync_ReadsBodyOnceUsingProviderLimit()
    {
        var calls = new List<string>();
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-limit", "known.event", includeSignature: false);
        var reader = new RecordingBodyReader(calls, body);
        using var provider = BuildProvider(
            providerBodyLimit: 7,
            bodyReader: reader,
            signatureVerifier: new RecordingSignatureVerifier(calls),
            timestampVerifier: new RecordingTimestampVerifier(calls),
            eventIdExtractor: new RecordingEventIdExtractor(calls, "evt-limit"),
            eventTypeExtractor: new RecordingEventTypeExtractor(calls, "known.event"),
            registerHandler: false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        await service.ProcessAsync(context, CreateEndpointOptions());

        reader.ReadCount.Should().Be(1);
        reader.MaxSizes.Should().Equal(7L);
    }

    [Fact]
    public async Task ProcessAsync_GeneratesClockBackedUlidShape()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-ulid", "known.event");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Context!.WebhookId.Should().MatchRegex("^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{26}$");
        var second = await service.ProcessAsync(CreateRequest(body, "evt-ulid-2", "known.event"), CreateEndpointOptions());
        second.Context!.WebhookId.Should().NotBe(result.Context.WebhookId);
    }

    [Fact]
    public void IdGenerator_UsesClockForUlidTimestampPrefix()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var generator = new WebhookIdGenerator(clock);
        var first = generator.Create();
        clock.Advance(TimeSpan.FromDays(1));
        var second = generator.Create();

        first.Should().MatchRegex("^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{26}$");
        first[..9].Should().Be("01M39MJVG");
        second.Should().MatchRegex("^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{26}$");
        second[..10].Should().NotBe(first[..10]);
    }

    [Fact]
    public async Task ProcessAsync_TimestampPrefixedSignature_UsesExactTimestampBytesAndConfiguredSeparator()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var timestamp = " " + FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + " ";
        var signedInput = Encoding.UTF8.GetBytes(timestamp + "|" + Encoding.UTF8.GetString(body));
        var signature = ComputeSignature(signedInput);
        var context = CreateRequest(body, "evt-prefixed", "known.event", includeSignature: false);
        context.Request.Headers[TimestampHeader] = timestamp;
        context.Request.Headers[SignatureHeader] = signature;
        using var provider = BuildProvider(configureProvider: provider =>
        {
            provider.Signature.Input = WebhookSignatureInput.TimestampPrefixedRawBody;
            provider.Signature.TimestampSeparator = "|";
        });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
    }

    [Fact]
    public async Task ProcessAsync_TimestampPrefixedSignature_UsesDefaultSeparator()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var timestamp = FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = ComputeSignature(Encoding.UTF8.GetBytes(timestamp + "." + Encoding.UTF8.GetString(body)));
        var context = CreateRequest(body, "evt-default-separator", "known.event", includeSignature: false);
        context.Request.Headers[TimestampHeader] = timestamp;
        context.Request.Headers[SignatureHeader] = signature;
        using var provider = BuildProvider(configureProvider: provider => provider.Signature.Input = WebhookSignatureInput.TimestampPrefixedRawBody);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
    }

    [Fact]
    public async Task ProcessAsync_ExplicitTimestampOptOut_AllowsMissingTimestampHeader()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-opt-out", "known.event", includeTimestamp: false);
        using var provider = BuildProvider(configureProvider: provider => provider.Timestamp.AllowMissing = true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        result.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ProcessAsync_SyncSuccessCanDiscardRawBodyAfterTerminalPersistence()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-discard", "known.event");
        var handler = new RecordingHandler();
        using var provider = BuildProvider(
            handler: handler,
            configureStorage: storage =>
            {
                storage.PersistRawBody = true;
                storage.DiscardRawBodyAfterSuccessfulSync = true;
            });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync(result.Context!.WebhookId);
        record!.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.RawBody.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_SyncIgnoredSuccessCanDiscardPersistedRawBody()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-ignored-discard", "unregistered.event");
        using var provider = BuildProvider(
            configureStorage: storage =>
            {
                storage.PersistRawBody = true;
                storage.DiscardRawBodyAfterSuccessfulSync = true;
            });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Ignored);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-ignored-discard");
        record!.Status.Should().Be(WebhookProcessingStatus.Ignored);
        record.RawBody.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_SyncAdmissionWithoutRawBodyPersistence_StillDispatchesFromContext()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-no-persist-sync", "known.event");
        var handler = new RecordingHandler();
        using var provider = BuildProvider(handler: handler, configureStorage: storage => storage.PersistRawBody = false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        handler.Calls.Should().Be(1);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetByWebhookIdAsync(result.Context!.WebhookId);
        record!.RawBody.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_ProcessingFailure_ReturnsSafeServerError()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-failure", "known.event");
        using var provider = BuildProvider(processor: new FailingDispatchProcessor());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.ProcessingFailed);
        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        result.Code.Should().Be("handler-failed");
        result.Message.Should().NotContain("exception");
        result.ToString().Should().NotContain("exception");
    }

    [Fact]
    public async Task ProcessAsync_RetryableDispatchExhaustion_ReturnsProcessingFailureAndPersistsFailedState()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-retry-exhaustion", "known.event");
        var processor = new RetryExhaustingDispatchProcessor();
        using var provider = BuildProvider(processor: processor, retryDelay: new ImmediateRetryDelay());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.ProcessingFailed);
        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        processor.Calls.Should().Be(3);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-retry-exhaustion");
        record!.Status.Should().Be(WebhookProcessingStatus.Failed);
        record.AttemptCount.Should().Be(3);
        record.FailureCode.Should().Be("retryable-failure");
        record.FailureReason.Should().NotContain("secret retry detail");
    }

    [Fact]
    public async Task ProcessAsync_CancellationDuringDispatchReleasesSynchronousClaim()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-sync-operation-cancel", "known.event");
        var processor = new BlockingDispatchProcessor();
        using var provider = BuildProvider(processor: processor);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        using var cancellation = new CancellationTokenSource();

        var operation = service.ProcessAsync(context, CreateEndpointOptions(), cancellation.Token);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        var act = async () => await operation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-sync-operation-cancel");
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
        record.ProcessingLeaseOwner.Should().BeNull();
        record.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_CancellationDuringRetryDelayReleasesSynchronousClaim()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-sync-delay-cancel", "known.event");
        var delay = new BlockingRetryDelay();
        var processor = new RetryExhaustingDispatchProcessor();
        using var provider = BuildProvider(processor: processor, retryDelay: delay);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        using var cancellation = new CancellationTokenSource();

        var operation = service.ProcessAsync(context, CreateEndpointOptions(), cancellation.Token);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        var act = async () => await operation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        processor.Calls.Should().Be(1);
        var record = await scope.ServiceProvider.GetRequiredService<IWebhookStore>().GetAsync(ProviderName, "evt-sync-delay-cancel");
        record!.Status.Should().Be(WebhookProcessingStatus.Received);
        record.ProcessingLeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_PayloadFailure_ReturnsBadRequestWithoutParserDetails()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-payload-failure", "known.event");
        using var provider = BuildProvider(processor: new PayloadFailingDispatchProcessor());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.Outcome.Should().Be(WebhookEndpointOutcome.PayloadInvalid);
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Code.Should().Be("payload-invalid");
        result.Message.Should().NotContain("parser");
        result.Message.Should().NotContain("exception");
    }

    [Fact]
    public async Task ProcessAsync_StatusCodeOverride_OnlyChangesStatus()
    {
        var body = Encoding.UTF8.GetBytes("{\"value\":42}");
        var context = CreateRequest(body, "evt-override", "known.event");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var options = CreateEndpointOptions();
        options = new WebhookEndpointOptions
        {
            ProviderName = options.ProviderName,
            Mode = options.Mode,
            Response = new WebhookEndpointResponseOptions
            {
                SuccessStatusCode = 218
            }
        };

        var result = await service.ProcessAsync(context, options);

        result.StatusCode.Should().Be(218);
        result.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        result.Message.Should().Be("Webhook processed.");
    }

    [Fact]
    public void MapWebhook_SimpleAndAdvancedOverloads_MapPostRoutesWithMetadata()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWebhookKit(options => ConfigureProvider(options));
        builder.Services.AddWebhookKitAspNetCore();
        using var app = builder.Build();

        app.MapWebhook("/webhooks/simple", ProviderName);
        var advancedOptions = new WebhookEndpointOptions
        {
            ProviderName = ProviderName,
            OperationId = "receive-endpoint",
            Summary = "Receive endpoint",
            Description = "Receives provider deliveries",
            IncludeInSchema = false
        };
        app.MapWebhook("/webhooks/advanced", advancedOptions);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/webhooks/", StringComparison.Ordinal) == true)
            .ToArray();

        endpoints.Should().HaveCount(2);
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().BeEquivalentTo("/webhooks/simple", "/webhooks/advanced");
        endpoints.Should().OnlyContain(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
        endpoints.Should().Contain(endpoint => endpoint.Metadata.GetMetadata<WebhookEndpointMetadata>() != null);
        var advanced = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/webhooks/advanced");
        var endpointMetadata = advanced.Metadata.GetMetadata<WebhookEndpointMetadata>()!;
        endpointMetadata.ProviderName.Should().Be(ProviderName);
        endpointMetadata.Options.Should().NotBeSameAs(advancedOptions);
        advanced.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName.Should().Be("receive-endpoint");
        advanced.Metadata.GetMetadata<EndpointSummaryAttribute>()!.Summary.Should().Be("Receive endpoint");
        advanced.Metadata.GetMetadata<EndpointDescriptionAttribute>()!.Description.Should().Be("Receives provider deliveries");
        advanced.Metadata.Any(metadata => metadata.GetType().Name.Contains("ExcludeFromDescription", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public async Task MapWebhook_PostRouteCompletesWithEndpointStatus()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWebhookKit(options => ConfigureProvider(options));
        builder.Services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        builder.Services.AddWebhookKitAspNetCore();
        builder.Services.AddWebhookHandler<RecordingHandler>("known.event");
        using var app = builder.Build();
        app.MapWebhook("/webhooks/route", ProviderName);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/webhooks/route");
        using var scope = app.Services.CreateScope();
        var context = CreateRequest(Encoding.UTF8.GetBytes("{\"value\":42}"), "evt-route", "known.event");
        context.RequestServices = scope.ServiceProvider;

        await endpoint.RequestDelegate!(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task ProcessAsync_ResultAndMessageNeverContainSensitiveInput()
    {
        var secretBody = "{\"secret\":\"do-not-expose\"}";
        var body = Encoding.UTF8.GetBytes(secretBody);
        var context = CreateRequest(body, "evt-safe", "known.event", signature: "invalid-secret-signature");
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();

        var result = await service.ProcessAsync(context, CreateEndpointOptions());

        result.ToString().Should().NotContain(secretBody);
        result.ToString().Should().NotContain("invalid-secret-signature");
        result.Message.Should().NotContain(secretBody);
        result.Message.Should().NotContain("invalid-secret-signature");
    }

    private static ServiceProvider BuildProvider(
        IWebhookBodyReader? bodyReader = null,
        IWebhookQueue? queue = null,
        IWebhookSignatureVerifier? signatureVerifier = null,
        IWebhookTimestampVerifier? timestampVerifier = null,
        IWebhookEventIdExtractor? eventIdExtractor = null,
        IWebhookEventTypeExtractor? eventTypeExtractor = null,
        IWebhookDeserializer? deserializer = null,
        IWebhookDispatchProcessor? processor = null,
        RecordingHandler? handler = null,
        bool registerHandler = true,
        long? providerBodyLimit = 1024,
        Action<WebhookProviderOptions>? configureProvider = null,
        Action<WebhookStorageOptions>? configureStorage = null,
        IWebhookRetryDelay? retryDelay = null)
    {
        var services = new ServiceCollection();
        services.AddWebhookKit(options =>
        {
            options.MaxRequestBodySizeBytes = 1024;
            options.Providers.Clear();
            options.AddProvider(ProviderName, provider =>
            {
                provider.MaxRequestBodySizeBytes = providerBodyLimit;
                provider.Signature.HeaderName = SignatureHeader;
                provider.Signature.Secret = Secret;
                provider.Timestamp.HeaderName = TimestampHeader;
                provider.EventIdHeaderName = EventIdHeader;
                provider.EventTypeHeaderName = EventTypeHeader;
                configureProvider?.Invoke(provider);
            });
            configureStorage?.Invoke(options.Storage);
        });
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        if (bodyReader is not null)
        {
            services.AddSingleton(bodyReader);
        }

        if (retryDelay is not null)
        {
            services.AddSingleton(retryDelay);
        }

        services.AddWebhookKitAspNetCore();
        if (signatureVerifier is not null)
        {
            services.AddSingleton(signatureVerifier);
        }

        if (timestampVerifier is not null)
        {
            services.AddSingleton(timestampVerifier);
        }

        if (eventIdExtractor is not null)
        {
            services.AddSingleton(eventIdExtractor);
        }

        if (eventTypeExtractor is not null)
        {
            services.AddSingleton(eventTypeExtractor);
        }

        if (deserializer is not null)
        {
            services.AddSingleton(deserializer);
        }

        if (processor is not null)
        {
            services.AddSingleton(processor);
        }

        if (queue is not null)
        {
            services.AddSingleton(queue);
        }
        else
        {
            services.RemoveAll<IWebhookQueue>();
        }

        if (registerHandler)
        {
            services.AddWebhookHandler<RecordingHandler>("known.event");
            if (handler is not null)
            {
                services.AddSingleton(handler);
            }
        }

        return services.BuildServiceProvider();
    }

    private static void ConfigureProvider(WebhookKitOptions options)
    {
        options.AddProvider(ProviderName, provider =>
        {
            provider.Signature.HeaderName = SignatureHeader;
            provider.Signature.Secret = Secret;
            provider.Timestamp.HeaderName = TimestampHeader;
            provider.EventIdHeaderName = EventIdHeader;
            provider.EventTypeHeaderName = EventTypeHeader;
        });
    }

    private static WebhookEndpointOptions CreateEndpointOptions(
        WebhookProcessingMode mode = WebhookProcessingMode.Synchronous)
    {
        return new WebhookEndpointOptions
        {
            ProviderName = ProviderName,
            Mode = mode
        };
    }

    private static DefaultHttpContext CreateRequest(
        byte[] body,
        string? eventId,
        string? eventType,
        DateTimeOffset? timestamp = null,
        bool includeTimestamp = true,
        bool includeSignature = true,
        string? signature = null)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "endpoint-trace";
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/webhooks/endpoint";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        if (eventId is not null)
        {
            context.Request.Headers[EventIdHeader] = eventId;
        }

        if (eventType is not null)
        {
            context.Request.Headers[EventTypeHeader] = eventType;
        }

        if (includeTimestamp)
        {
            var value = (timestamp ?? FixedNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Headers[TimestampHeader] = value;
        }

        if (includeSignature)
        {
            context.Request.Headers[SignatureHeader] = signature ?? ComputeSignature(body);
        }

        return context;
    }

    private static string ComputeSignature(byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    public sealed class RecordingHandler : IWebhookHandler<EndpointPayload>
    {
        public int Calls { get; private set; }

        public Task HandleAsync(EndpointPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            _ = context.GetPayload<EndpointPayload>();
            Calls++;
            return Task.CompletedTask;
        }
    }

    public sealed record EndpointPayload(int Value);

    private sealed class RecordingBodyReader(List<string> calls, byte[] body) : IWebhookBodyReader
    {
        public int ReadCount { get; private set; }

        public List<long> MaxSizes { get; } = [];

        public ValueTask<byte[]> ReadRawBodyAsync(HttpContext context, long maxSizeBytes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            calls.Add("body");
            MaxSizes.Add(maxSizeBytes);
            return ValueTask.FromResult(body.ToArray());
        }
    }

    private sealed class RecordingSignatureVerifier(List<string> calls) : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("signature");
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }
    }

    private sealed class RecordingTimestampVerifier(List<string> calls) : IWebhookTimestampVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("timestamp");
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }
    }

    private sealed class RecordingEventIdExtractor(List<string> calls, string? value) : IWebhookEventIdExtractor
    {
        public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("event-id");
            return ValueTask.FromResult<string?>(value);
        }
    }

    private sealed class RecordingEventTypeExtractor(List<string> calls, string? value) : IWebhookEventTypeExtractor
    {
        public ValueTask<string?> ExtractAsync(WebhookVerificationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("event-type");
            return ValueTask.FromResult<string?>(value);
        }
    }

    private sealed class RecordingDeserializer(List<string> calls) : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            calls.Add("deserialize");
            return default!;
        }
    }

    private sealed class BlockingRetryDelay : IWebhookRetryDelay
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class BlockingDispatchProcessor : IWebhookDispatchProcessor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WebhookDispatchResult.Processed();
        }
    }

    private sealed class ImmediateRetryDelay : IWebhookRetryDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RetryExhaustingDispatchProcessor : IWebhookDispatchProcessor
    {
        public int Calls { get; private set; }

        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            throw new WebhookKit.Abstractions.Exceptions.WebhookRetryableException("secret retry detail");
        }
    }

    private sealed class FailingDispatchProcessor : IWebhookDispatchProcessor
    {
        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed"));
        }
    }

    private sealed class PayloadFailingDispatchProcessor : IWebhookDispatchProcessor
    {
        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Payload, "payload-invalid"));
        }
    }

    private sealed class RecordingQueue : IWebhookQueue
    {
        public bool EnqueueResult { get; init; } = true;

        public List<WebhookWorkItem> Items { get; } = [];

        public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnqueueResult)
            {
                Items.Add(item);
            }

            return ValueTask.FromResult(EnqueueResult);
        }

        public ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}
