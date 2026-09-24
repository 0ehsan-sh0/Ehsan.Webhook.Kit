using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using WebhookKit.Abstractions;
using WebhookKit.Core.Options;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookTestRequestBuilderTests
{
    private const string Provider = "payments";
    private const string EventId = "evt_123";
    private const string EventType = "payment.completed";
    private const string Secret = "test-secret";
    private const string RotationSecret = "previous-secret";
    private const string ExactTimestamp = "001700000000";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] ExactBody = [123, 34, 118, 97, 108, 117, 101, 34, 58, 52, 50, 125, 10];

    [Fact]
    public void TestRequest_WithMethodsReturnIndependentSnapshots()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var original = WebhookTestRequestBuilder.Create(Provider, clock)
            .WithEventId("evt_original")
            .WithEventType(EventType);

        var changed = original
            .WithEventId("evt_changed")
            .WithHeader("X-Trace", "changed");

        using var originalRequest = original.Build();
        using var changedRequest = changed.Build();

        originalRequest.Headers.GetValues("X-Webhook-Event-Id").Should().Equal("evt_original");
        changedRequest.Headers.GetValues("X-Webhook-Event-Id").Should().Equal("evt_changed");
        changedRequest.Headers.GetValues("X-Trace").Should().Equal("changed");
        original.Headers.Should().NotContainKey("X-Trace");
    }

    [Fact]
    public void TestRequest_HeaderSnapshotIsDetachedAndReadOnly()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithHeaderValues("X-Trace", "one", "two");
        var snapshot = builder.Headers;
        var values = (IList<string>)snapshot["X-Trace"];
        var mutate = () => values.Add("blocked");
        mutate.Should().Throw<NotSupportedException>();
        builder.WithHeader("X-Trace", "three");
        snapshot["X-Trace"].Should().Equal("one", "two");
    }

    [Fact]
    public async Task TestRequest_BuildCopiesExactRawBytesAndContentType()
    {
        var bytes = new byte[] { 0, 255, 65, 10, 13 };
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(bytes)
            .WithContentType("application/custom");

        using var request = builder.Build();
        var body = await request.Content!.ReadAsByteArrayAsync();

        body.Should().Equal(bytes);
        request.Content.Headers.ContentType!.ToString().Should().Be("application/custom");
        bytes[0] = 99;
        (await request.Content.ReadAsByteArrayAsync()).Should().Equal(0, 255, 65, 10, 13);
    }

    [Fact]
    public async Task TestRequest_PreservesDuplicateHeaderValues()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(ExactBody)
            .WithHeaderValues("X-Trace", "one", "two")
            .WithHeader("X-Trace", "three");

        using var request = builder.Build();

        request.Headers.GetValues("X-Trace").Should().Equal("one", "two", "three");
        (await request.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
    }

    [Fact]
    public async Task TestRequest_DefaultTimestampUsesInjectedClock()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(ExactBody);

        using var request = builder.Build();

        request.Headers.GetValues("X-Webhook-Timestamp").Should().Equal("1790208000");
        request.Headers.GetValues("X-Webhook-Provider").Should().Equal(Provider);
        (await request.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
    }

    [Fact]
    public async Task TestRequest_ExplicitTimestampTextIsNotNormalized()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(ExactBody)
            .WithTimestamp(" 1700000000 ");

        using var request = builder.Build();

        request.Headers.GetValues("X-Webhook-Timestamp").Should().Equal(" 1700000000 ");
        (await request.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
    }

    [Fact]
    public void TestRequest_DateTimeOffsetUsesUnixSeconds()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithTimestamp(FixedNow);

        using var request = builder.Build();

        request.Headers.GetValues("X-Webhook-Timestamp").Should().Equal("1790208000");
    }

    [Fact]
    public void TestRequest_HeaderNamesAreCustomizable()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithEventId(EventId)
            .WithEventType(EventType)
            .WithTimestamp(ExactTimestamp)
            .WithProviderHeaderName("X-P")
            .WithEventIdHeaderName("X-ID")
            .WithEventTypeHeaderName("X-Type")
            .WithTimestampHeaderName("X-Time");

        using var request = builder.Build();

        request.Headers.GetValues("X-P").Should().Equal(Provider);
        request.Headers.GetValues("X-ID").Should().Equal(EventId);
        request.Headers.GetValues("X-Type").Should().Equal(EventType);
        request.Headers.GetValues("X-Time").Should().Equal(ExactTimestamp);
        request.Headers.Contains("X-Webhook-Provider").Should().BeFalse();
    }

    [Fact]
    public async Task TestRequest_JsonIsSerializedBeforeBytesAreUsed()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithJson(new TestPayload(42), options);

        using var request = builder.Build();
        var body = await request.Content!.ReadAsByteArrayAsync();

        body.Should().Equal(Encoding.UTF8.GetBytes("{\"eventValue\":42}"));
        request.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task TestRequest_RawModeBypassesJsonAndSerializerOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var bytes = new byte[] { 0xFF, 0x00, 0x7B };
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithJson(new TestPayload(42), options)
            .WithRawBody(bytes)
            .WithContentType("application/octet-stream");

        using var request = builder.Build();
        var body = await request.Content!.ReadAsByteArrayAsync();

        body.Should().Equal(bytes);
        request.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData(WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Hex, "0d31f421896d1cbdbadbf365daf0d8f480541dac8733afea3996682a65a59b0c")]
    [InlineData(WebhookHashAlgorithm.HmacSha256, WebhookSignatureEncoding.Base64, "DTH0IYltHL262/Nl2vDY9IBUHayHM6/qOZZoKmWlmww=")]
    [InlineData(WebhookHashAlgorithm.HmacSha512, WebhookSignatureEncoding.Hex, "bc1d3a437c219b8d06a3496a8a29645e3db131d28619025fa0d24ef105a8031d56b5a3593047a288f9c1d29045dd42da9a0017cc1e3df5bf7318d550791c6dc6")]
    [InlineData(WebhookHashAlgorithm.HmacSha512, WebhookSignatureEncoding.Base64, "vB06Q3whm40Go0lqiilkXj2xMdKGGQJfoNJO8QWoAx1WtaNZMEeiiPnB0pBF3ULamgAXzB499b9zGNVQeRxtxg==")]
    public void TestRequest_SupportsBothAlgorithmsAndEncodings(
        WebhookHashAlgorithm algorithm,
        WebhookSignatureEncoding encoding,
        string expected)
    {
        var generator = new WebhookSignatureGenerator(Secret, algorithm, encoding);

        generator.Generate(ExactBody).Should().Be(expected);
    }

    [Fact]
    public void TestRequest_UsesExactTimestampPrefixAndCustomSeparator()
    {
        var generator = new WebhookSignatureGenerator(
            Secret,
            WebhookHashAlgorithm.HmacSha256,
            WebhookSignatureEncoding.Hex,
            WebhookSignatureInput.TimestampPrefixedRawBody,
            "|");

        generator.Generate(ExactBody, ExactTimestamp).Should().Be("0a03a408113270e13a9d4653117d3b3b46aea8e1db9a7905f6c56be9a0754e06");
        generator.Generate(ExactBody, " 1700000000 ").Should().Be("62b0c4113675c6703998d7391c41d77cb4d02444737c4d2034db9c6f5991c45e");
    }

    [Fact]
    public void TestRequest_SupportsExplicitCustomSigningInput()
    {
        var generator = new WebhookSignatureGenerator(Secret);
        var customInput = Encoding.UTF8.GetBytes("custom:001700000000:{\"value\":42}\n");

        var signature = generator.GenerateCustom(customInput);
        var callbackSignature = generator.GenerateWithCustomInput(
            ExactBody,
            ExactTimestamp,
            (body, timestamp) => Encoding.UTF8.GetBytes($"custom:{timestamp}:{Encoding.UTF8.GetString(body.Span)}"));

        signature.Should().Be("d4b1630fe1e200f7766c8b84ac6a2ce8f4feaae81356f308c6ccce47f61c94f5");
        callbackSignature.Should().Be(signature);
        generator.VerifyCustom(signature, customInput).Should().BeTrue();
    }

    [Fact]
    public void TestRequest_VerificationAcceptsPrimaryAndRotationSecretsInOrder()
    {
        var primaryGenerator = new WebhookSignatureGenerator(Secret);
        var rotatedGenerator = new WebhookSignatureGenerator(RotationSecret);
        var verifyingGenerator = new WebhookSignatureGenerator(
            Secret,
            WebhookHashAlgorithm.HmacSha256,
            WebhookSignatureEncoding.Hex,
            WebhookSignatureInput.RawBody,
            rotationSecrets: [RotationSecret]);

        var primarySignature = primaryGenerator.Generate(ExactBody);
        var rotatedSignature = rotatedGenerator.Generate(ExactBody);

        primarySignature.Should().NotBe(rotatedSignature);
        verifyingGenerator.Generate(ExactBody).Should().Be(primarySignature);
        verifyingGenerator.Verify(primarySignature, ExactBody).Should().BeTrue();
        verifyingGenerator.Verify(rotatedSignature, ExactBody).Should().BeTrue();
        verifyingGenerator.Verify("wrong", ExactBody).Should().BeFalse();
    }

    [Fact]
    public void TestRequest_VerificationRejectsMalformedAndWrongSignaturesWithoutSecretDetails()
    {
        var secret = "do-not-expose-this-secret";
        var generator = new WebhookSignatureGenerator(secret, rotationSecrets: ["another-secret"]);

        var wrongResult = generator.VerifyResult("wrong-signature", ExactBody);
        var malformedResult = generator.VerifyResult("not-valid-hex", ExactBody);

        wrongResult.IsValid.Should().BeFalse();
        malformedResult.IsValid.Should().BeFalse();
        wrongResult.FailureReason.Should().Be(malformedResult.FailureReason);
        wrongResult.FailureReason.Should().NotContain(secret).And.NotContain("another-secret");
        generator.ToString().Should().NotContain(secret).And.NotContain("another-secret");
    }

    [Fact]
    public async Task TestRequest_CustomSigningInputCannotMutateFrozenBody()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(bytes)
            .WithTimestamp(ExactTimestamp);
        var generator = new WebhookSignatureGenerator(Secret);

        using var request = builder.BuildSigned(
            generator,
            (rawBody, _) =>
            {
                MemoryMarshal.TryGetArray(rawBody, out var segment);
                segment.Array![0] = 99;
                return rawBody;
            });

        (await request.Content!.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task TestRequest_BuilderSignsTheExactTimestampAndRawBody()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(ExactBody)
            .WithTimestamp(ExactTimestamp)
            .WithSignatureHeaderName("X-Signature");
        var generator = new WebhookSignatureGenerator(
            Secret,
            WebhookHashAlgorithm.HmacSha256,
            WebhookSignatureEncoding.Hex,
            WebhookSignatureInput.TimestampPrefixedRawBody,
            "|");

        using var request = builder.BuildSigned(generator);

        request.Headers.GetValues("X-Webhook-Timestamp").Should().Equal(ExactTimestamp);
        request.Headers.GetValues("X-Signature").Should().Equal("0a03a408113270e13a9d4653117d3b3b46aea8e1db9a7905f6c56be9a0754e06");
        request.Headers.Contains("X-Webhook-Signature").Should().BeFalse();
        (await request.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
    }

    [Fact]
    public void TestRequest_ClearingOptionalMetadataDoesNotMutatePriorBuilder()
    {
        var original = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithEventId(EventId)
            .WithEventType(EventType)
            .WithJson(new TestPayload(42));

        var cleared = original
            .WithEventId(null)
            .WithEventType(null)
            .WithJsonSerializerOptions(null);

        cleared.EventId.Should().BeNull();
        cleared.EventType.Should().BeNull();
        cleared.JsonSerializerOptions.Should().BeNull();
        original.EventId.Should().Be(EventId);
        original.EventType.Should().Be(EventType);
    }

    [Fact]
    public void TestRequest_VerificationUsesTheSamePrefixOptions()
    {
        var signatureGenerator = new WebhookSignatureGenerator(
            Secret,
            WebhookHashAlgorithm.HmacSha512,
            WebhookSignatureEncoding.Base64,
            WebhookSignatureInput.TimestampPrefixedRawBody,
            "~");
        var signature = signatureGenerator.Generate(ExactBody, ExactTimestamp);
        var verifier = new WebhookSignatureGenerator(
            Secret,
            WebhookHashAlgorithm.HmacSha512,
            WebhookSignatureEncoding.Base64,
            WebhookSignatureInput.TimestampPrefixedRawBody,
            "~");

        verifier.Verify(signature, ExactBody, ExactTimestamp).Should().BeTrue();
        verifier.Verify($"sha512={signature}", ExactBody, ExactTimestamp).Should().BeTrue();
        verifier.Verify(signature, ExactBody, ExactTimestamp, WebhookSignatureInput.TimestampPrefixedRawBody, "!").Should().BeFalse();
    }

    [Fact]
    public async Task TestRequest_BuilderRemainsReusableAfterSigning()
    {
        var builder = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithEventId(EventId)
            .WithEventType(EventType)
            .WithRawBody(ExactBody)
            .WithTimestamp(ExactTimestamp);
        var generator = new WebhookSignatureGenerator(Secret);

        using var first = builder.BuildSigned(generator);
        using var second = builder.BuildSigned(generator);
        first.Headers.Add("X-Only-First", "value");

        first.Headers.GetValues("X-Webhook-Signature").Should().Equal("0d31f421896d1cbdbadbf365daf0d8f480541dac8733afea3996682a65a59b0c");
        second.Headers.GetValues("X-Webhook-Signature").Should().Equal("0d31f421896d1cbdbadbf365daf0d8f480541dac8733afea3996682a65a59b0c");
        second.Headers.Contains("X-Only-First").Should().BeFalse();
        (await first.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
        (await second.Content!.ReadAsByteArrayAsync()).Should().Equal(ExactBody);
    }

    [Fact]
    public async Task TestRequest_HarnessStartsSendsStopsAndDisposesIdempotently()
    {
        var host = new RecordingTestHost();
        var harness = new WebhookTestHarness(() => host);
        using var request = WebhookTestRequestBuilder.Create(Provider, new FakeWebhookClock(FixedNow))
            .WithRawBody(ExactBody)
            .WithTimestamp(ExactTimestamp)
            .BuildSigned(new WebhookSignatureGenerator(Secret));

        var beforeStart = () => harness.CreateClient();
        beforeStart.Should().Throw<InvalidOperationException>();

        await harness.StartAsync();
        await harness.StartAsync();
        using (var client = harness.CreateClient())
        using (var response = await harness.SendAsync(request))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            host.Requests.Should().ContainSingle();
            host.Requests[0].Method.Should().Be(HttpMethod.Post);
            host.Bodies.Should().ContainSingle();
            host.Bodies[0].Should().Equal(ExactBody);
            host.StartCount.Should().Be(1);
        }

        await harness.StopAsync();
        await harness.StopAsync();
        await harness.DisposeAsync();
        await harness.DisposeAsync();
        host.StopCount.Should().Be(1);

        var afterDispose = () => harness.CreateClient();
        afterDispose.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task TestRequest_HarnessForwardsCancellationAndRejectsUseAfterDispose()
    {
        var host = new RecordingTestHost(new BlockingHandler());
        var harness = new WebhookTestHarness(() => host);
        using var startCancellation = new CancellationTokenSource();
        await harness.StartAsync(startCancellation.Token);
        host.StartToken.Should().Be(startCancellation.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook");
        using var sendCancellation = new CancellationTokenSource();
        var send = harness.SendAsync(request, sendCancellation.Token);
        var handler = host.Handler;
        handler.Should().NotBeNull();
        await handler!.Entered;
        sendCancellation.Cancel();

        Func<Task> act = async () => await send;
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.LastToken.CanBeCanceled.Should().BeTrue();
        handler.LastToken.IsCancellationRequested.Should().BeTrue();

        using var stopCancellation = new CancellationTokenSource();
        await harness.StopAsync(stopCancellation.Token);
        host.StopToken.Should().Be(stopCancellation.Token);
        harness.Dispose();
        harness.Dispose();
        Func<Task> afterDispose = () => harness.SendAsync(request);
        await afterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    private sealed record TestPayload(int EventValue);

    private sealed class RecordingTestHost : IWebhookTestHost
    {
        private readonly HttpMessageHandler? _handler;

        public RecordingTestHost(HttpMessageHandler? handler = null)
        {
            _handler = handler;
        }

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public CancellationToken StartToken { get; private set; }
        public CancellationToken StopToken { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<byte[]> Bodies { get; } = [];
        public BlockingHandler? Handler => _handler as BlockingHandler;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartToken = cancellationToken;
            StartCount++;
            return Task.CompletedTask;
        }

        public HttpClient CreateClient()
        {
            return new HttpClient(_handler ?? new RecordingHandler(this))
            {
                BaseAddress = new Uri("http://localhost")
            };
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopToken = cancellationToken;
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(object? owner = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner is RecordingTestHost host)
            {
                host.Requests.Add(request);
                if (request.Content is not null)
                {
                    host.Bodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public Task Entered => _entered.Task;
        public CancellationToken LastToken { get; private set; }

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _entered.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(
                _ => new HttpResponseMessage(HttpStatusCode.Accepted),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
