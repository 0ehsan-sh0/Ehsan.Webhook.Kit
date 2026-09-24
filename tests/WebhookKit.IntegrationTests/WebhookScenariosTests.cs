using System.Net;
using System.Text.Json;
using FluentAssertions;
using WebhookKit.Abstractions;
using Xunit;

namespace WebhookKit.IntegrationTests;

public sealed class WebhookScenariosTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private const string MinimalPath = "/webhooks/minimal";
    private const string AsyncPath = "/webhooks/async";
    private const string MvcPath = "/mvc/valid";

    [Fact]
    public async Task ValidSignedWebhook_IsProcessedAndHandlerReceivesTypedPayload()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-valid",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");

        await AssertBodylessAsync(response, HttpStatusCode.OK);
        application.HandlerProbe.Invocations.Should().Be(1);
        application.HandlerProbe.Successes.Should().Be(1);
        application.HandlerProbe.Calls.Should().ContainSingle();
        application.HandlerProbe.Calls[0].Payload.Value.Should().Be(42);
        application.HandlerProbe.Calls[0].Context.EventId.Should().Be("evt-valid");
        application.HandlerProbe.Calls[0].Context.EventType.Should().Be(WebhookKitTestApplication.KnownEventType);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-valid", WebhookProcessingStatus.Processed, 1);
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task TamperedSignature_ReturnsUnauthorizedWithoutHandlerOrRecord()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-tampered",
            WebhookKitTestApplication.KnownEventType,
            signature: "tampered-signature");

        await AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "signature-verification-failed",
            "Webhook signature verification failed.");
        application.HandlerProbe.Invocations.Should().Be(0);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        application.RecordCount.Should().Be(0);
        (await application.GetRecordAsync("evt-tampered")).Should().BeNull();
    }

    [Fact]
    public async Task ExpiredTimestamp_ReturnsBadRequestWithoutHandlerOrRecord()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-expired",
            WebhookKitTestApplication.KnownEventType,
            timestamp: WebhookKitTestApplication.FixedNow.AddMinutes(-6));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "timestamp-verification-failed",
            "Webhook timestamp verification failed.");
        application.HandlerProbe.Invocations.Should().Be(0);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        application.RecordCount.Should().Be(0);
        (await application.GetRecordAsync("evt-expired")).Should().BeNull();
    }

    [Fact]
    public async Task DuplicateSync_ReturnsOkWithoutSecondHandlerExecution()
    {
        await using var application = await CreateApplicationAsync();
        using var first = await application.SendSignedAsync(
            MinimalPath,
            "evt-duplicate",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");
        using var second = await application.SendSignedAsync(
            MinimalPath,
            "evt-duplicate",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");

        await AssertBodylessAsync(first, HttpStatusCode.OK);
        await AssertBodylessAsync(second, HttpStatusCode.OK);
        application.HandlerProbe.Invocations.Should().Be(1);
        application.HandlerProbe.Successes.Should().Be(1);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-duplicate", WebhookProcessingStatus.Processed, 1);
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task UnknownEventType_ReturnsOkAndPersistsIgnored()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-unknown",
            "unknown.event",
            "{\"value\":42}");

        await AssertBodylessAsync(response, HttpStatusCode.OK);
        application.HandlerProbe.Invocations.Should().Be(0);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-unknown", WebhookProcessingStatus.Ignored, 1);
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task MalformedJson_ReturnsSafeBadRequestWithoutSuccessfulHandlerPayload()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-malformed",
            WebhookKitTestApplication.KnownEventType,
            "{invalid");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "payload-invalid",
            "Webhook payload is invalid.",
            "{invalid");
        application.HandlerProbe.Invocations.Should().Be(0);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-malformed", WebhookProcessingStatus.Failed, 1, "payload-invalid");
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task OversizedBody_ReturnsPayloadTooLargeWithoutRecord()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-oversized",
            WebhookKitTestApplication.KnownEventType,
            new string('x', 2048));

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            "payload-too-large",
            "Webhook payload is too large.");
        application.HandlerProbe.Invocations.Should().Be(0);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        application.RecordCount.Should().Be(0);
        (await application.GetRecordAsync("evt-oversized")).Should().BeNull();
    }

    [Fact]
    public async Task RetryableFailure_RetriesWithControlledDelayAndSucceeds()
    {
        await using var application = await CreateApplicationAsync(new WebhookKitTestApplicationOptions
        {
            HandlerBehavior = WebhookKitHandlerBehavior.FailThenSucceed,
            FailuresBeforeSuccess = 2,
            UseControlledRetryDelay = true
        });
        var send = application.SendSignedAsync(
            MinimalPath,
            "evt-retry-success",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");

        var firstDelay = await application.WaitForRetryDelayAsync();
        var processing = await application.GetRecordAsync("evt-retry-success");
        processing.Should().NotBeNull();
        processing!.Status.Should().Be(WebhookProcessingStatus.Processing);
        processing.AttemptCount.Should().Be(1);
        application.HandlerProbe.Invocations.Should().Be(1);
        firstDelay.Release();

        var secondDelay = await application.WaitForRetryDelayAsync();
        var secondAttempt = await application.GetRecordAsync("evt-retry-success");
        secondAttempt.Should().NotBeNull();
        secondAttempt!.Status.Should().Be(WebhookProcessingStatus.Processing);
        secondAttempt.AttemptCount.Should().Be(2);
        application.HandlerProbe.Invocations.Should().Be(2);
        secondDelay.Release();

        using var response = await send.WaitAsync(TestTimeout);
        await AssertBodylessAsync(response, HttpStatusCode.OK);
        application.RetryDelayProbe.Delays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        application.HandlerProbe.Invocations.Should().Be(3);
        application.HandlerProbe.Successes.Should().Be(1);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-retry-success", WebhookProcessingStatus.Processed, 3);
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task RetryExhaustion_ReturnsServerErrorAndPersistsFailed()
    {
        await using var application = await CreateApplicationAsync(new WebhookKitTestApplicationOptions
        {
            HandlerBehavior = WebhookKitHandlerBehavior.AlwaysFail
        });
        using var response = await application.SendSignedAsync(
            MinimalPath,
            "evt-retry-exhaustion",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");

        await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "handler-failed",
            "Webhook processing failed.",
            "sensitive integration retry detail");
        application.RetryDelayProbe.Delays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        application.HandlerProbe.Invocations.Should().Be(3);
        application.HandlerProbe.Successes.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        await AssertRecordAsync(application, "evt-retry-exhaustion", WebhookProcessingStatus.Failed, 3, "retryable-failure");
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task AsyncMode_ReturnsAcceptedThenWorkerMarksProcessed()
    {
        await using var application = await CreateApplicationAsync(new WebhookKitTestApplicationOptions
        {
            EnableBackgroundWorker = true
        });
        using var response = await application.SendSignedAsync(
            AsyncPath,
            "evt-async",
            WebhookKitTestApplication.KnownEventType,
            "{\"value\":42}");

        await AssertBodylessAsync(response, HttpStatusCode.Accepted);
        var call = await application.WaitForHandlerAsync();
        var record = await application.WaitForTerminalAsync();
        call.Context.EventId.Should().Be("evt-async");
        call.Payload.Value.Should().Be(42);
        record.EventId.Should().Be("evt-async");
        record.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.AttemptCount.Should().Be(1);
        application.HandlerProbe.Invocations.Should().Be(1);
        application.HandlerProbe.Successes.Should().Be(1);
        application.ActionProbe.Invocations.Should().Be(0);
        application.QueueProbe.EnqueuedCount.Should().Be(1);
        application.QueueProbe.DequeuedCount.Should().Be(1);
        application.RecordCount.Should().Be(1);
    }

    [Fact]
    public async Task MvcEndpoint_MatchesMinimalStatusBodyAndExecution()
    {
        await using var application = await CreateApplicationAsync();

        using var validMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-valid-minimal", WebhookKitTestApplication.KnownEventType, "{\"value\":42}");
        using var validMvc = await application.SendSignedAsync(MvcPath, "evt-parity-valid-mvc", WebhookKitTestApplication.KnownEventType, "{\"value\":42}");
        await AssertParityAsync(validMinimal, validMvc, HttpStatusCode.OK);
        await AssertBodylessAsync(validMinimal, HttpStatusCode.OK);
        await AssertBodylessAsync(validMvc, HttpStatusCode.OK);

        using var tamperedMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-tampered-minimal", WebhookKitTestApplication.KnownEventType, signature: "tampered-signature");
        using var tamperedMvc = await application.SendSignedAsync(MvcPath, "evt-parity-tampered-mvc", WebhookKitTestApplication.KnownEventType, signature: "tampered-signature");
        await AssertParityAsync(tamperedMinimal, tamperedMvc, HttpStatusCode.Unauthorized);
        await AssertProblemAsync(tamperedMinimal, HttpStatusCode.Unauthorized, "signature-verification-failed", "Webhook signature verification failed.");

        using var expiredMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-expired-minimal", WebhookKitTestApplication.KnownEventType, timestamp: WebhookKitTestApplication.FixedNow.AddMinutes(-6));
        using var expiredMvc = await application.SendSignedAsync(MvcPath, "evt-parity-expired-mvc", WebhookKitTestApplication.KnownEventType, timestamp: WebhookKitTestApplication.FixedNow.AddMinutes(-6));
        await AssertParityAsync(expiredMinimal, expiredMvc, HttpStatusCode.BadRequest);
        await AssertProblemAsync(expiredMinimal, HttpStatusCode.BadRequest, "timestamp-verification-failed", "Webhook timestamp verification failed.");

        using var malformedMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-malformed-minimal", WebhookKitTestApplication.KnownEventType, "{invalid");
        using var malformedMvc = await application.SendSignedAsync(MvcPath, "evt-parity-malformed-mvc", WebhookKitTestApplication.KnownEventType, "{invalid");
        await AssertParityAsync(malformedMinimal, malformedMvc, HttpStatusCode.BadRequest);
        await AssertProblemAsync(malformedMinimal, HttpStatusCode.BadRequest, "payload-invalid", "Webhook payload is invalid.", "{invalid");

        using var unknownMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-unknown-minimal", "unknown.event", "{\"value\":42}");
        using var unknownMvc = await application.SendSignedAsync(MvcPath, "evt-parity-unknown-mvc", "unknown.event", "{\"value\":42}");
        await AssertParityAsync(unknownMinimal, unknownMvc, HttpStatusCode.OK);
        await AssertBodylessAsync(unknownMinimal, HttpStatusCode.OK);
        await AssertBodylessAsync(unknownMvc, HttpStatusCode.OK);

        using var oversizedMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-oversized-minimal", WebhookKitTestApplication.KnownEventType, new string('x', 2048));
        using var oversizedMvc = await application.SendSignedAsync(MvcPath, "evt-parity-oversized-mvc", WebhookKitTestApplication.KnownEventType, new string('x', 2048));
        await AssertParityAsync(oversizedMinimal, oversizedMvc, HttpStatusCode.RequestEntityTooLarge);
        await AssertProblemAsync(oversizedMinimal, HttpStatusCode.RequestEntityTooLarge, "payload-too-large", "Webhook payload is too large.");

        using var duplicateMinimal = await application.SendSignedAsync(MinimalPath, "evt-parity-duplicate", WebhookKitTestApplication.KnownEventType, "{\"value\":42}");
        using var duplicateMvc = await application.SendSignedAsync(MvcPath, "evt-parity-duplicate", WebhookKitTestApplication.KnownEventType, "{\"value\":42}");
        await AssertParityAsync(duplicateMinimal, duplicateMvc, HttpStatusCode.OK);
        await AssertBodylessAsync(duplicateMinimal, HttpStatusCode.OK);
        await AssertBodylessAsync(duplicateMvc, HttpStatusCode.OK);

        application.HandlerProbe.Invocations.Should().Be(3);
        application.HandlerProbe.Successes.Should().Be(3);
        application.ActionProbe.Invocations.Should().Be(1);
        await AssertRecordAsync(application, "evt-parity-valid-minimal", WebhookProcessingStatus.Processed, 1);
        await AssertRecordAsync(application, "evt-parity-valid-mvc", WebhookProcessingStatus.Processed, 1);
        await AssertRecordAsync(application, "evt-parity-malformed-minimal", WebhookProcessingStatus.Failed, 1, "payload-invalid");
        await AssertRecordAsync(application, "evt-parity-malformed-mvc", WebhookProcessingStatus.Failed, 1, "payload-invalid");
        await AssertRecordAsync(application, "evt-parity-unknown-minimal", WebhookProcessingStatus.Ignored, 1);
        await AssertRecordAsync(application, "evt-parity-unknown-mvc", WebhookProcessingStatus.Ignored, 1);
        await AssertRecordAsync(application, "evt-parity-duplicate", WebhookProcessingStatus.Processed, 1);
        application.RecordCount.Should().Be(7);
        (await application.GetRecordAsync("evt-parity-tampered-minimal")).Should().BeNull();
        (await application.GetRecordAsync("evt-parity-tampered-mvc")).Should().BeNull();
        (await application.GetRecordAsync("evt-parity-expired-minimal")).Should().BeNull();
        (await application.GetRecordAsync("evt-parity-expired-mvc")).Should().BeNull();
        (await application.GetRecordAsync("evt-parity-oversized-minimal")).Should().BeNull();
        (await application.GetRecordAsync("evt-parity-oversized-mvc")).Should().BeNull();
    }

    [Fact]
    public async Task CustomExtractors_UseNestedJsonPathsAndRouteCustomHandler()
    {
        await using var application = await CreateApplicationAsync(new WebhookKitTestApplicationOptions
        {
            UseCustomExtractors = true,
            RegisterCustomHandler = true
        });
        const string body = "{\"data\":{\"id\":\"evt-custom\",\"type\":\"custom.event\"},\"value\":42}";
        using var response = await application.SendSignedWithoutMetadataAsync(
            "/webhooks/custom",
            body);

        await AssertBodylessAsync(response, HttpStatusCode.OK);
        application.CustomExtractorProbe.EventIdCalls.Should().Be(1);
        application.CustomExtractorProbe.EventTypeCalls.Should().Be(1);
        application.CustomHandlerProbe.Invocations.Should().Be(1);
        application.CustomHandlerProbe.Successes.Should().Be(1);
        application.HandlerProbe.Invocations.Should().Be(0);
        application.ActionProbe.Invocations.Should().Be(0);
        var record = await application.GetRecordAsync("evt-custom");
        record.Should().NotBeNull();
        record!.EventType.Should().Be(WebhookKitTestApplication.CustomEventType);
        record.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.AttemptCount.Should().Be(1);
        application.RecordCount.Should().Be(1);
    }

    private static async Task<WebhookKitTestApplication> CreateApplicationAsync(
        WebhookKitTestApplicationOptions? options = null)
    {
        var application = new WebhookKitTestApplication(options);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        await application.StartAsync(cancellation.Token);
        return application;
    }

    private static async Task AssertBodylessAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        string expectedMessage,
        string? sensitiveInput = null)
    {
        response.StatusCode.Should().Be(expectedStatus);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        document.RootElement.GetProperty("message").GetString().Should().Be(expectedMessage);
        document.RootElement.GetProperty("traceId").GetString().Should().Be(WebhookKitTestApplication.TraceId);
        body.Should().NotContain(WebhookKitTestApplication.Secret);
        body.Should().NotContain("exception");
        if (sensitiveInput is not null)
        {
            body.Should().NotContain(sensitiveInput);
        }
    }

    private static async Task AssertParityAsync(
        HttpResponseMessage minimal,
        HttpResponseMessage mvc,
        HttpStatusCode expectedStatus)
    {
        minimal.StatusCode.Should().Be(expectedStatus);
        mvc.StatusCode.Should().Be(minimal.StatusCode);
        minimal.Content.Headers.ContentType?.MediaType.Should().Be(mvc.Content.Headers.ContentType?.MediaType);
        (await mvc.Content.ReadAsStringAsync()).Should().Be(await minimal.Content.ReadAsStringAsync());
    }

    private static async Task AssertRecordAsync(
        WebhookKitTestApplication application,
        string eventId,
        WebhookProcessingStatus expectedStatus,
        int expectedAttempts,
        string? expectedFailureCode = null)
    {
        var record = await application.GetRecordAsync(eventId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(expectedStatus);
        record.AttemptCount.Should().Be(expectedAttempts);
        record.EventId.Should().Be(eventId);
        if (expectedFailureCode is not null)
        {
            record.FailureCode.Should().Be(expectedFailureCode);
        }
    }
}
