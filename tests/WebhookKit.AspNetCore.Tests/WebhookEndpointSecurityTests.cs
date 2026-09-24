using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Exceptions;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.AspNetCore.Responses;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Processing;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

[CollectionDefinition("WebhookEndpointSecurity", DisableParallelization = true)]
public sealed class WebhookEndpointSecurityTestGroup
{
}

[Collection("WebhookEndpointSecurity")]
public sealed class WebhookEndpointSecurityTests
{
    public const string ProviderName = "security-endpoint-provider";
    private const string EventType = "security.event";
    private const int BodyLimit = 64;
    private const string BodyMarker = "sec-audit-endpoint-raw-body-marker-8d71";
    private const string HeaderMarker = "sec-audit-endpoint-authorization-marker-43c2";
    private const string SignatureMarker = "sec-audit-endpoint-signature-marker-65af";
    private const string ConfiguredSecretMarker = "sec-audit-endpoint-configured-secret-marker-29e4";
    private const string RotationSecretMarker = "sec-audit-endpoint-rotation-secret-marker-71b6";
    private const string VerifierReasonMarker = "sec-audit-endpoint-verifier-reason-marker-90af";
    private const string ParserDetailMarker = "sec-audit-endpoint-parser-detail-marker-17cd";
    private const string HandlerMessageMarker = "sec-audit-endpoint-handler-message-marker-a4e8";
    private const string DependencyMessageMarker = "sec-audit-endpoint-dependency-message-marker-5c9e";
    private const string StackTextMarker = "sec-audit-endpoint-stack-text-marker-f3b5";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadRawBodyAsync_SecurityAdvertisedOversizeRejectsBeforeReadingOrBackingTheBody()
    {
        var context = new DefaultHttpContext();
        var stream = new VirtualReadStream(1_000_000_000);
        context.Request.Body = stream;
        context.Request.ContentLength = 1_000_000_000;
        var reader = new WebhookBodyReader();

        Func<Task> act = async () => await reader.ReadRawBodyAsync(context, BodyLimit);

        var exception = await act.Should().ThrowAsync<WebhookPayloadTooLargeException>();
        exception.Which.ActualBytes.Should().Be(1_000_000_000);
        exception.Which.MaxBytes.Should().Be(BodyLimit);
        stream.ReadCount.Should().Be(0);
        stream.BytesReturned.Should().Be(0);
    }

    [Fact]
    public async Task ReadRawBodyAsync_SecurityStreamedOversizeStopsAtOneByteBeyondLimitAndBoundsEveryRead()
    {
        const int limit = 31;
        var context = new DefaultHttpContext();
        var stream = new VirtualReadStream(1_000_000_000);
        context.Request.Body = stream;
        context.Request.ContentLength = null;
        var reader = new WebhookBodyReader();

        Func<Task> act = async () => await reader.ReadRawBodyAsync(context, limit);

        var exception = await act.Should().ThrowAsync<WebhookPayloadTooLargeException>();
        exception.Which.ActualBytes.Should().Be(limit + 1);
        exception.Which.MaxBytes.Should().Be(limit);
        stream.BytesReturned.Should().Be(limit + 1);
        stream.MaxRequested.Should().BeLessThanOrEqualTo(limit + 1);
    }

    [Fact]
    public async Task MinimalApi_AllSignatureFailureModes_ReturnAndLogIndistinguishableSafeOutcomes()
    {
        var logs = new WebhookEndpointLoggingTests.CapturingLoggerProvider();
        var probe = new SecurityActionProbe();
        using var host = CreateHost(logs, probe, SecurityFailureMode.Success);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(activities);
        var requests = new[]
        {
            CreateSignatureFailureRequest("signature-tampered", "tampered"),
            CreateSignatureFailureRequest("signature-malformed", "malformed"),
            CreateSignatureFailureRequest("signature-wrong-secret", "wrong-secret"),
            CreateSignatureFailureRequest("signature-encoding", "encoding-mismatch")
        };
        var problems = new List<WebhookProblemResponse>();

        foreach (var request in requests)
        {
            using var response = await host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            problems.Add(ReadProblem(body));
            AssertNoMarkers(body, SecurityMarkers());
        }

        problems.Select(problem => problem.Code).Distinct().Should().Equal(["signature-verification-failed"]);
        problems.Select(problem => problem.Message).Distinct().Should().Equal(["Webhook signature verification failed."]);
        var endpointEntries = logs.Entries
            .Where(entry => entry.Category == typeof(WebhookEndpointService).FullName)
            .ToArray();
        endpointEntries.Should().HaveCount(4);
        endpointEntries.Select(entry => entry.Template).Distinct().Should().ContainSingle();
        endpointEntries.Select(entry => string.Join("|", entry.State.Select(pair => pair.Key))).Distinct().Should().ContainSingle();
        endpointEntries.Should().OnlyContain(entry => entry.EventId.Id == 1811);
        endpointEntries.Should().OnlyContain(entry => Equals(entry.Get("FailureCode"), "signature-verification-failed"));
        endpointEntries.Should().OnlyContain(entry => Equals(entry.Get("Outcome"), nameof(WebhookEndpointOutcome.InvalidSignature)));
        endpointEntries.Should().OnlyContain(entry => Equals(entry.Get("Status"), 401));
        var logText = string.Join("|", logs.Entries.Select(FormatLog));
        activities.Should().HaveCount(4);
        AssertNoMarkers($"{logText}|{FormatActivities(activities)}", SecurityMarkers());
        probe.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("minimal", "invalid-signature", 401, "signature-verification-failed")]
    [InlineData("mvc", "invalid-signature", 401, "signature-verification-failed")]
    [InlineData("minimal", "payload-invalid", 400, "payload-invalid")]
    [InlineData("mvc", "payload-invalid", 400, "payload-invalid")]
    [InlineData("minimal", "payload-too-large", 413, "payload-too-large")]
    [InlineData("mvc", "payload-too-large", 413, "payload-too-large")]
    [InlineData("minimal", "queue-unavailable", 503, "queue-unavailable")]
    [InlineData("mvc", "queue-unavailable", 503, "queue-unavailable")]
    public async Task RealTestServerPaths_ExpectedSecurityOutcomes_ReturnSafeProblemResponses(
        string surface,
        string scenario,
        int expectedStatus,
        string expectedCode)
    {
        var logs = new WebhookEndpointLoggingTests.CapturingLoggerProvider();
        var probe = new SecurityActionProbe();
        using var host = CreateHost(logs, probe, SecurityFailureMode.Success);
        using var request = CreateOutcomeRequest(surface, scenario);
        using var activitiesListener = StartActivityListener(new ConcurrentQueue<Activity>());

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be((HttpStatusCode)expectedStatus);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        var problem = ReadProblem(body);
        problem.Code.Should().Be(expectedCode);
        AssertNoMarkers($"{body}|{string.Join("|", logs.Entries.Select(FormatLog))}", SecurityMarkers());
        probe.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("minimal", false, 200)]
    [InlineData("mvc", false, 200)]
    [InlineData("minimal", true, 202)]
    [InlineData("mvc", true, 202)]
    public async Task RealTestServerPaths_SuccessAcknowledgements_AreEmptyAndDoNotExposeMarkers(
        string surface,
        bool asynchronous,
        int expectedStatus)
    {
        var logs = new WebhookEndpointLoggingTests.CapturingLoggerProvider();
        var probe = new SecurityActionProbe();
        using var host = CreateHost(logs, probe, SecurityFailureMode.Success, includeQueue: asynchronous);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(activities);
        var path = surface == "minimal"
            ? asynchronous ? "/security-audit/minimal-async" : "/security-audit/minimal"
            : asynchronous ? "/security-audit/mvc-async" : "/security-audit/mvc";
        using var request = CreateSignedRequest(path, $"ack-{surface}-{asynchronous}", $"{{\"raw\":\"{BodyMarker}\",\"value\":42}}");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be((HttpStatusCode)expectedStatus);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().BeEmpty();
        activities.Should().NotBeEmpty();
        AssertNoMarkers(
            $"{body}|{string.Join("|", logs.Entries.Select(FormatLog))}|{FormatActivities(activities)}",
            SecurityMarkers());
    }

    [Theory]
    [InlineData("minimal", SecurityFailureMode.Handler)]
    [InlineData("mvc", SecurityFailureMode.Handler)]
    [InlineData("minimal", SecurityFailureMode.Dependency)]
    [InlineData("mvc", SecurityFailureMode.Dependency)]
    public async Task RealTestServerPaths_HandlerAndDependencyFailures_DoNotEscapeAsUnhandledServerErrors(
        string surface,
        SecurityFailureMode failureMode)
    {
        var logs = new WebhookEndpointLoggingTests.CapturingLoggerProvider();
        var probe = new SecurityActionProbe();
        using var host = CreateHost(logs, probe, failureMode);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(activities);
        var path = surface == "minimal" ? "/security-audit/minimal" : "/security-audit/mvc";
        using var request = CreateSignedRequest(path, $"failure-{failureMode}-{surface}", $"{{\"raw\":\"{BodyMarker}\",\"value\":42}}");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        var problem = ReadProblem(body);
        problem.Code.Should().Be("handler-failed");
        problem.Message.Should().Be("Webhook processing failed.");
        activities.Should().NotBeEmpty();
        AssertNoMarkers(
            $"{body}|{string.Join("|", logs.Entries.Select(FormatLog))}|{FormatActivities(activities)}",
            SecurityMarkers());
        logs.Entries.Should().OnlyContain(entry => entry.Exception == null);
        probe.Calls.Should().Be(0);
    }

    private static SecurityTestHost CreateHost(
        WebhookEndpointLoggingTests.CapturingLoggerProvider logs,
        SecurityActionProbe probe,
        SecurityFailureMode failureMode,
        bool includeQueue = false)
    {
        var builder = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(probe);
                services.AddWebhookKit(options =>
                {
                    options.MaxRequestBodySizeBytes = BodyLimit;
                    options.AddProvider(ProviderName, provider =>
                    {
                        provider.MaxRequestBodySizeBytes = BodyLimit;
                        provider.Signature.HeaderName = WebhookTestRequestBuilder.DefaultSignatureHeaderName;
                        provider.Signature.Secret = ConfiguredSecretMarker;
                        provider.Signature.AdditionalSecrets.Add(RotationSecretMarker);
                        provider.Timestamp.HeaderName = WebhookTestRequestBuilder.DefaultTimestampHeaderName;
                        provider.EventIdHeaderName = WebhookTestRequestBuilder.DefaultEventIdHeaderName;
                        provider.EventTypeHeaderName = WebhookTestRequestBuilder.DefaultEventTypeHeaderName;
                    });
                });
                services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
                services.AddSingleton<ILogger<WebhookEndpointService>>(logs.CreateLogger<WebhookEndpointService>());
                services.AddSingleton<ILogger<WebhookIngestionService>>(logs.CreateLogger<WebhookIngestionService>());
                services.AddSingleton<ILogger<WebhookProcessor>>(logs.CreateLogger<WebhookProcessor>());
                services.AddWebhookKitAspNetCore();
                switch (failureMode)
                {
                    case SecurityFailureMode.Success:
                        services.AddWebhookHandler<SuccessfulSecurityHandler>(EventType);
                        break;
                    case SecurityFailureMode.Handler:
                        services.AddWebhookHandler<ThrowingSecurityHandler>(EventType);
                        break;
                    case SecurityFailureMode.Dependency:
                        services.AddSingleton<ISecurityFailureDependency, ThrowingSecurityDependency>();
                        services.AddWebhookHandler<DependencySecurityHandler>(EventType);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(failureMode));
                }

                if (includeQueue)
                {
                    services.AddSingleton<IWebhookQueue, SecurityQueueSink>();
                }
                else
                {
                    services.RemoveAll<IWebhookQueue>();
                }

                services.AddControllers().AddApplicationPart(typeof(SecurityMvcController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapWebhook("/security-audit/minimal", ProviderName);
                    endpoints.MapWebhook("/security-audit/minimal-async", new WebhookEndpointOptions(ProviderName)
                    {
                        Mode = WebhookProcessingMode.Asynchronous
                    });
                });
            });
        return new SecurityTestHost(new TestServer(builder), probe);
    }

    private static HttpRequestMessage CreateSignatureFailureRequest(string eventId, string failureMode)
    {
        byte[] body = Encoding.UTF8.GetBytes($"{{\"raw\":\"{BodyMarker}\",\"value\":42}}");
        var builder = CreateBuilder("/security-audit/minimal", eventId, EventType, body);
        return failureMode switch
        {
            "tampered" => builder.WithSignature(TamperHex(ComputeSignature(body, ConfiguredSecretMarker, WebhookKit.Core.Options.WebhookSignatureEncoding.Hex))).Build(),
            "malformed" => builder.WithSignature(SignatureMarker).Build(),
            "wrong-secret" => builder.BuildSigned("security-endpoint-wrong-secret"),
            "encoding-mismatch" => builder.BuildSigned(new WebhookSignatureGenerator(
                ConfiguredSecretMarker,
                encoding: WebhookKit.Core.Options.WebhookSignatureEncoding.Base64)),
            _ => throw new ArgumentOutOfRangeException(nameof(failureMode))
        };
    }

    private static HttpRequestMessage CreateOutcomeRequest(string surface, string scenario)
    {
        var asynchronous = scenario == "queue-unavailable";
        var path = surface == "minimal"
            ? asynchronous ? "/security-audit/minimal-async" : "/security-audit/minimal"
            : asynchronous ? "/security-audit/mvc-async" : "/security-audit/mvc";
        var body = scenario switch
        {
            "payload-too-large" => Encoding.UTF8.GetBytes(new string('x', BodyLimit + 1)),
            "payload-invalid" => Encoding.UTF8.GetBytes($"{{\"raw\":\"{BodyMarker}\",\"value\":"),
            _ => Encoding.UTF8.GetBytes($"{{\"raw\":\"{BodyMarker}\",\"value\":42}}")
        };
        if (scenario == "invalid-signature")
        {
            return CreateBuilder(path, $"expected-{surface}-{scenario}", EventType, body)
                .WithSignature(SignatureMarker)
                .Build();
        }

        return CreateSignedRequest(path, $"expected-{surface}-{scenario}", Encoding.UTF8.GetString(body));
    }

    private static HttpRequestMessage CreateSignedRequest(string path, string eventId, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        return CreateBuilder(path, eventId, EventType, bodyBytes)
            .BuildSigned(new WebhookSignatureGenerator(ConfiguredSecretMarker));
    }

    private static WebhookTestRequestBuilder CreateBuilder(string path, string eventId, string eventType, byte[] body)
    {
        return WebhookTestRequestBuilder
            .Create(ProviderName, eventId, eventType, new FakeWebhookClock(FixedNow))
            .WithPath(path)
            .WithTimestamp(FixedNow)
            .WithRawBody(body)
            .WithContentType("application/json")
            .WithHeader("Authorization", HeaderMarker)
            .WithHeader("X-Provider-Signature", SignatureMarker);
    }

    private static string ComputeSignature(
        byte[] body,
        string secret,
        WebhookKit.Core.Options.WebhookSignatureEncoding encoding)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return encoding == WebhookKit.Core.Options.WebhookSignatureEncoding.Hex
            ? Convert.ToHexString(hash).ToLowerInvariant()
            : Convert.ToBase64String(hash);
    }

    private static string TamperHex(string signature)
    {
        return (signature[0] == '0' ? '1' : '0') + signature[1..];
    }

    private static WebhookProblemResponse ReadProblem(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new WebhookProblemResponse
        {
            Code = root.GetProperty("code").GetString()!,
            Message = root.GetProperty("message").GetString()!,
            TraceId = root.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null
        };
    }

    private static string FormatLog(WebhookEndpointLoggingTests.CapturedLog entry)
    {
        return string.Join(
            "|",
            entry.Template,
            entry.Formatted,
            string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}")),
            entry.Exception?.ToString() ?? string.Empty);
    }

    private static string FormatActivities(IEnumerable<Activity> activities)
    {
        return string.Join("|", activities.SelectMany(activity => new[]
        {
            activity.DisplayName,
            activity.ToString(),
            string.Join("|", activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")),
            string.Join("|", activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")))
        }));
    }

    private static ActivityListener StartActivityListener(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WebhookKit" && source.Version == "1.0.0",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("webhook.provider"), ProviderName))
                {
                    activities.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string[] SecurityMarkers()
    {
        return
        [
            BodyMarker,
            HeaderMarker,
            SignatureMarker,
            ConfiguredSecretMarker,
            RotationSecretMarker,
            VerifierReasonMarker,
            ParserDetailMarker,
            HandlerMessageMarker,
            DependencyMessageMarker,
            StackTextMarker
        ];
    }

    private static void AssertNoMarkers(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            text.Should().NotContain(marker);
        }
    }

    public enum SecurityFailureMode
    {
        Success,
        Handler,
        Dependency
    }

    public sealed record SecurityPayload(int Value);

    public sealed class SecurityQueueSink : IWebhookQueue
    {
        public List<WebhookWorkItem> Items { get; } = [];

        public ValueTask<bool> TryEnqueueAsync(
            WebhookWorkItem workItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(workItem);
            return ValueTask.FromResult(true);
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

    public interface ISecurityFailureDependency
    {
        void Fail();
    }

    public sealed class SuccessfulSecurityHandler : IWebhookHandler<SecurityPayload>
    {
        public Task HandleAsync(
            SecurityPayload eventData,
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingSecurityHandler : IWebhookHandler<SecurityPayload>
    {
        public Task HandleAsync(
            SecurityPayload eventData,
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            throw new MarkedEndpointException($"{HandlerMessageMarker}|{ParserDetailMarker}");
        }
    }

    public sealed class DependencySecurityHandler(ISecurityFailureDependency dependency) : IWebhookHandler<SecurityPayload>
    {
        public Task HandleAsync(
            SecurityPayload eventData,
            WebhookContext context,
            CancellationToken cancellationToken = default)
        {
            dependency.Fail();
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingSecurityDependency : ISecurityFailureDependency
    {
        public void Fail()
        {
            throw new MarkedEndpointException($"{DependencyMessageMarker}|{ParserDetailMarker}");
        }
    }

    private sealed class MarkedEndpointException(string message) : Exception(message)
    {
        public override string? StackTrace => $"at {StackTextMarker}(SecurityBoundary)";
    }

    public sealed class SecurityActionProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Record()
        {
            Interlocked.Increment(ref _calls);
        }
    }

    private sealed class SecurityTestHost : IDisposable
    {
        private readonly TestServer _server;

        public SecurityTestHost(TestServer server, SecurityActionProbe probe)
        {
            _server = server;
            Probe = probe;
            Client = server.CreateClient();
        }

        public HttpClient Client { get; }

        public SecurityActionProbe Probe { get; }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
        }
    }

    private sealed class VirtualReadStream(long virtualLength) : Stream
    {
        private long _bytesReturned;
        private int _maxRequested;

        public int ReadCount { get; private set; }

        public long BytesReturned => Interlocked.Read(ref _bytesReturned);

        public int MaxRequested => Volatile.Read(ref _maxRequested);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => virtualLength;

        public override long Position
        {
            get => BytesReturned;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = virtualLength - BytesReturned;
            if (available <= 0 || count == 0)
            {
                return 0;
            }

            var bytesRead = (int)Math.Min(count, available);
            ReadCount++;
            UpdateMax(ref _maxRequested, count);
            Interlocked.Add(ref _bytesReturned, bytesRead);
            Array.Clear(buffer, offset, bytesRead);
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesReturned >= virtualLength || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }

            var count = (int)Math.Min(buffer.Length, virtualLength - BytesReturned);
            ReadCount++;
            UpdateMax(ref _maxRequested, buffer.Length);
            Interlocked.Add(ref _bytesReturned, count);
            buffer.Span[..count].Clear();
            return ValueTask.FromResult(count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private static void UpdateMax(ref int maximum, int value)
        {
            var current = Volatile.Read(ref maximum);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref maximum, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}

[ApiController]
[Route("security-audit")]
public sealed class SecurityMvcController(WebhookEndpointSecurityTests.SecurityActionProbe probe) : ControllerBase
{
    [HttpPost("mvc")]
    [WebhookEndpoint(WebhookEndpointSecurityTests.ProviderName)]
    public IActionResult Mcv(WebhookContext context)
    {
        probe.Record();
        return Ok();
    }

    [HttpPost("mvc-async")]
    [WebhookEndpoint(WebhookEndpointSecurityTests.ProviderName, Mode = WebhookProcessingMode.Asynchronous)]
    public IActionResult McvAsync(WebhookContext context)
    {
        probe.Record();
        return Accepted();
    }
}
