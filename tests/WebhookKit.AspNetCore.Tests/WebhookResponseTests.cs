using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.AspNetCore.Responses;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

public sealed class WebhookResponseTests
{
    private const string ProviderName = "response-provider";
    private const string KnownEventType = "known.event";
    private const string BrokenEventType = "broken.event";
    private const string SignatureHeader = "X-Signature";
    private const string TimestampHeader = "X-Timestamp";
    private const string EventIdHeader = "X-Event-Id";
    private const string EventTypeHeader = "X-Event-Type";
    private const string Secret = "response-secret-value";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<WebhookEndpointOutcome, string, string> FormatterCases => new()
    {
        { WebhookEndpointOutcome.Processed, "processed", "Webhook processed." },
        { WebhookEndpointOutcome.Accepted, "accepted", "Webhook accepted." },
        { WebhookEndpointOutcome.Duplicate, "duplicate", "Webhook already received." },
        { WebhookEndpointOutcome.Ignored, "ignored", "Webhook ignored." },
        { WebhookEndpointOutcome.InvalidSignature, "signature-verification-failed", "Webhook signature verification failed." },
        { WebhookEndpointOutcome.InvalidTimestamp, "timestamp-verification-failed", "Webhook timestamp verification failed." },
        { WebhookEndpointOutcome.MissingEventId, "event-id-required", "Webhook event identifier is required." },
        { WebhookEndpointOutcome.MissingEventType, "missing-event-type", "Webhook event type is required." },
        { WebhookEndpointOutcome.PayloadInvalid, "payload-invalid", "Webhook payload is invalid." },
        { WebhookEndpointOutcome.PayloadTooLarge, "payload-too-large", "Webhook payload is too large." },
        { WebhookEndpointOutcome.QueueUnavailable, "queue-unavailable", "Webhook processing is temporarily unavailable." },
        { WebhookEndpointOutcome.ProcessingFailed, "handler-failed", "Webhook processing failed." },
        { WebhookEndpointOutcome.ConfigurationError, "webhook-configuration-error", "Webhook processing is not configured." }
    };

    public static TheoryData<string, int, string, string> ErrorCases => new()
    {
        { "invalid-signature", 401, "signature-verification-failed", "Webhook signature verification failed." },
        { "invalid-timestamp", 400, "timestamp-verification-failed", "Webhook timestamp verification failed." },
        { "missing-event-id", 400, "event-id-required", "Webhook event identifier is required." },
        { "missing-event-type", 400, "missing-event-type", "Webhook event type is required." },
        { "payload-invalid", 400, "payload-invalid", "Webhook payload is invalid." },
        { "payload-too-large", 413, "payload-too-large", "Webhook payload is too large." },
        { "queue-unavailable", 503, "queue-unavailable", "Webhook processing is temporarily unavailable." },
        { "processing-failed", 500, "handler-failed", "Webhook processing failed." },
        { "configuration-error", 500, "webhook-configuration-error", "Webhook processing is not configured." }
    };

    [Theory]
    [MemberData(nameof(FormatterCases))]
    public void DefaultFormatter_MapsEveryOutcomeToOneFixedProblem(
        WebhookEndpointOutcome outcome,
        string expectedCode,
        string expectedMessage)
    {
        var formatter = new DefaultWebhookResponseFormatter();

        var problem = formatter.Format(outcome, "formatter-trace");

        problem.Code.Should().Be(expectedCode);
        problem.Message.Should().Be(expectedMessage);
        problem.TraceId.Should().Be("formatter-trace");
    }

    [Fact]
    public void DefaultFormatter_RejectsUnknownOutcome()
    {
        var formatter = new DefaultWebhookResponseFormatter();

        var act = () => formatter.Format((WebhookEndpointOutcome)999, "trace");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task MinimalApi_EveryErrorOutcomeUsesExactStatusAndSafeProblem(
        string scenario,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        using var host = CreateHost(
            includeQueue: scenario != "queue-unavailable",
            processor: scenario == "processing-failed" ? new ThrowingDispatchProcessor() : null);

        using var response = await SendScenarioAsync(host.Client, scenario);

        await AssertProblemAsync(response, expectedStatus, expectedCode, expectedMessage);
    }

    [Fact]
    public async Task MinimalApi_ProcessedDuplicateAndIgnoredAreBodylessAcknowledgements()
    {
        using var host = CreateHost();

        using var processed = await host.Client.SendAsync(CreateRequest("/minimal", "evt-processed", KnownEventType));
        using var duplicate = await host.Client.SendAsync(CreateRequest("/minimal", "evt-processed", KnownEventType));
        using var ignored = await host.Client.SendAsync(CreateRequest("/minimal", "evt-ignored", "unknown.event"));

        await AssertBodylessAsync(processed, StatusCodes.Status200OK);
        await AssertBodylessAsync(duplicate, StatusCodes.Status200OK);
        await AssertBodylessAsync(ignored, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task MinimalApi_AsyncAcceptedAndDuplicateAreBodylessAcknowledgements()
    {
        using var host = CreateHost();

        using var accepted = await host.Client.SendAsync(CreateRequest("/minimal-async", "evt-async-response", KnownEventType));
        using var duplicate = await host.Client.SendAsync(CreateRequest("/minimal-async", "evt-async-response", KnownEventType));

        await AssertBodylessAsync(accepted, StatusCodes.Status202Accepted);
        await AssertBodylessAsync(duplicate, StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task Mvc_AndMinimalApi_ProduceTheSameErrorStatusAndBody()
    {
        using var host = CreateHost();

        using var minimal = await host.Client.SendAsync(CreateRequest("/minimal", "evt-parity-minimal", KnownEventType, signature: "bad-signature"));
        using var mvc = await host.Client.SendAsync(CreateRequest("/mvc-response/error", "evt-parity-mvc", KnownEventType, signature: "bad-signature"));
        var minimalBody = await minimal.Content.ReadAsStringAsync();
        var mvcBody = await mvc.Content.ReadAsStringAsync();

        mvc.StatusCode.Should().Be(minimal.StatusCode);
        mvc.Content.Headers.ContentType?.MediaType.Should().Be(minimal.Content.Headers.ContentType?.MediaType);
        mvcBody.Should().Be(minimalBody);
        await AssertProblemAsync(minimal, StatusCodes.Status401Unauthorized, "signature-verification-failed", "Webhook signature verification failed.");
    }

    [Fact]
    public async Task Mvc_AndMinimalApi_ProduceTheSameBodylessAcknowledgement()
    {
        using var host = CreateHost();

        using var minimal = await host.Client.SendAsync(CreateRequest("/minimal", "evt-parity-ack-minimal", KnownEventType));
        using var mvc = await host.Client.SendAsync(CreateRequest("/mvc-response/valid", "evt-parity-ack-mvc", KnownEventType));

        mvc.StatusCode.Should().Be(minimal.StatusCode);
        (await mvc.Content.ReadAsStringAsync()).Should().Be(await minimal.Content.ReadAsStringAsync());
        await AssertBodylessAsync(minimal, StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Response_TraceIdIsIncludedWhenHostSuppliesIt()
    {
        using var host = CreateHost();

        using var response = await host.Client.SendAsync(CreateRequest("/minimal", "evt-trace-present", KnownEventType, signature: "bad-signature"));

        await AssertProblemAsync(response, StatusCodes.Status401Unauthorized, "signature-verification-failed", "Webhook signature verification failed.");
    }

    [Fact]
    public async Task Response_TraceIdIsOmittedWhenNoHostOrActivityValueExists()
    {
        using var host = CreateHost();
        using var scope = host.Server.Services.CreateScope();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            var context = CreateDirectContext("evt-trace-absent", KnownEventType, "bad-signature", traceIdentifier: string.Empty);
            context.RequestServices = scope.ServiceProvider;

            await scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>().ProcessAsync(context, CreateEndpointOptions());

            var body = await ReadResponseBodyAsync(context);
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("traceId", out _).Should().BeFalse();
            body.Should().NotContain("00000000-0000-0000-0000-000000000000");
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task Response_UsesActivityTraceIdWhenHostIdentifierIsEmpty()
    {
        using var host = CreateHost();
        using var scope = host.Server.Services.CreateScope();
        using var activity = new Activity("response-test").Start();
        var context = CreateDirectContext("evt-activity-trace", KnownEventType, "bad-signature", traceIdentifier: string.Empty);
        context.RequestServices = scope.ServiceProvider;

        await scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>().ProcessAsync(context, CreateEndpointOptions());

        var body = await ReadResponseBodyAsync(context);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("traceId").GetString().Should().Be(activity.TraceId.ToString());
    }

    [Fact]
    public async Task Response_DoesNotExposeBodySignatureSecretParserOrExceptionValues()
    {
        var body = "{\"secret\":\"raw-body-secret\",\"detail\":\"parser-detail-secret\"}";
        using var host = CreateHost(processor: new ThrowingDispatchProcessor());

        using var response = await host.Client.SendAsync(CreateRequest("/minimal", "evt-sensitive", KnownEventType, body, "signature-secret-value"));
        var responseBody = await response.Content.ReadAsStringAsync();

        responseBody.Should().NotContain("raw-body-secret");
        responseBody.Should().NotContain("parser-detail-secret");
        responseBody.Should().NotContain("signature-secret-value");
        responseBody.Should().NotContain(Secret);
        responseBody.Should().NotContain("exception");
    }

    [Fact]
    public async Task Response_UsesProblemJsonContentTypeForErrors()
    {
        using var host = CreateHost();

        using var response = await host.Client.SendAsync(CreateRequest("/minimal", "evt-content-type", KnownEventType, signature: "bad-signature"));

        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Response_StatusOverrideChangesOnlyStatus()
    {
        var options = new WebhookEndpointOptions(ProviderName)
        {
            Response = new WebhookEndpointResponseOptions
            {
                SuccessStatusCode = 299,
                InvalidSignatureStatusCode = 422
            }
        };
        using var host = CreateHost(routeOptions: options);

        using var success = await host.Client.SendAsync(CreateRequest("/minimal-custom", "evt-override-success", KnownEventType));
        using var failure = await host.Client.SendAsync(CreateRequest("/minimal-custom", "evt-override-failure", KnownEventType, signature: "bad-signature"));

        await AssertBodylessAsync(success, 299);
        await AssertProblemAsync(failure, 422, "signature-verification-failed", "Webhook signature verification failed.");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public async Task Response_RejectsIllegalStatusOverrideBeforeProcessing(int statusCode)
    {
        using var host = CreateHost();
        using var scope = host.Server.Services.CreateScope();
        var context = CreateDirectContext("evt-invalid-status", KnownEventType, "bad-signature");
        context.RequestServices = scope.ServiceProvider;
        var options = new WebhookEndpointOptions(ProviderName)
        {
            Response = new WebhookEndpointResponseOptions
            {
                InvalidSignatureStatusCode = statusCode
            }
        };

        var act = () => scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>().ProcessAsync(context, options);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Response_CustomFormatterReplacesDefaultProblem()
    {
        using var host = CreateHost(formatter: new FixedFormatter());

        using var response = await host.Client.SendAsync(CreateRequest("/minimal", "evt-custom-formatter", KnownEventType, signature: "bad-signature"));

        await AssertProblemAsync(response, StatusCodes.Status401Unauthorized, "custom-code", "Custom safe message.");
    }

    [Fact]
    public async Task Response_CancellationPropagatesBeforeWriting()
    {
        using var host = CreateHost();
        using var scope = host.Server.Services.CreateScope();
        var context = CreateDirectContext("evt-response-cancelled", KnownEventType, "bad-signature");
        context.RequestServices = scope.ServiceProvider;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>().ProcessAsync(context, CreateEndpointOptions(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        context.Response.Body.Length.Should().Be(0);
    }

    private static ResponseTestHost CreateHost(
        bool includeQueue = true,
        IWebhookResponseFormatter? formatter = null,
        IWebhookDispatchProcessor? processor = null,
        WebhookEndpointOptions? routeOptions = null)
    {
        var builder = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((_, services) =>
            {
                services.AddWebhookKit(ConfigureProvider);
                if (formatter is not null)
                {
                    services.AddSingleton<IWebhookResponseFormatter>(formatter);
                }

                if (processor is not null)
                {
                    services.AddSingleton<IWebhookDispatchProcessor>(processor);
                }

                services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
                services.AddWebhookKitAspNetCore();
                services.AddWebhookHandler<ResponseHandler>(KnownEventType);
                services.AddWebhookHandler<BrokenResponseHandler>(BrokenEventType);
                if (includeQueue)
                {
                    services.AddSingleton<ResponseQueue>();
                    services.AddSingleton<IWebhookQueue>(sp => sp.GetRequiredService<ResponseQueue>());
                }
                else
                {
                    services.RemoveAll<IWebhookQueue>();
                }

                services.AddControllers().AddApplicationPart(typeof(ResponseController).Assembly);
            })
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.TraceIdentifier = "response-trace";
                    await next();
                });
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapWebhook("/minimal", ProviderName);
                    endpoints.MapWebhook("/minimal-async", new WebhookEndpointOptions(ProviderName)
                    {
                        Mode = WebhookProcessingMode.Asynchronous
                    });
                    endpoints.MapWebhook("/minimal-unknown", new WebhookEndpointOptions("unknown-provider"));
                    if (routeOptions is not null)
                    {
                        endpoints.MapWebhook("/minimal-custom", routeOptions);
                    }
                });
            });

        return new ResponseTestHost(new TestServer(builder));
    }

    private static Task<HttpResponseMessage> SendScenarioAsync(HttpClient client, string scenario)
    {
        return scenario switch
        {
            "invalid-signature" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", KnownEventType, signature: "bad-signature")),
            "invalid-timestamp" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", KnownEventType, timestamp: FixedNow.AddMinutes(-10))),
            "missing-event-id" => client.SendAsync(CreateRequest("/minimal", null, KnownEventType)),
            "missing-event-type" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", null)),
            "payload-invalid" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", BrokenEventType, body: "{invalid")),
            "payload-too-large" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", KnownEventType, body: new string('x', 2048))),
            "queue-unavailable" => client.SendAsync(CreateRequest("/minimal-async", $"evt-{scenario}", KnownEventType)),
            "processing-failed" => client.SendAsync(CreateRequest("/minimal", $"evt-{scenario}", KnownEventType)),
            "configuration-error" => client.SendAsync(CreateRequest("/minimal-unknown", $"evt-{scenario}", KnownEventType)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static async Task AssertBodylessAsync(HttpResponseMessage response, int expectedStatus)
    {
        response.StatusCode.Should().Be((HttpStatusCode)expectedStatus);
        response.Content.Headers.ContentType.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        int expectedStatus,
        string expectedCode,
        string expectedMessage)
    {
        response.StatusCode.Should().Be((HttpStatusCode)expectedStatus);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        properties.Should().BeEquivalentTo(["code", "message", "traceId"]);
        document.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        document.RootElement.GetProperty("message").GetString().Should().Be(expectedMessage);
        document.RootElement.GetProperty("traceId").GetString().Should().Be("response-trace");
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static DefaultHttpContext CreateDirectContext(
        string? eventId,
        string? eventType,
        string? signature,
        string traceIdentifier = "response-trace",
        string body = "{\"value\":42}")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/webhooks/response";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bodyBytes.Length;
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Response.Body = new MemoryStream();
        if (eventId is not null)
        {
            context.Request.Headers[EventIdHeader] = eventId;
        }

        if (eventType is not null)
        {
            context.Request.Headers[EventTypeHeader] = eventType;
        }

        context.Request.Headers[TimestampHeader] = FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Request.Headers[SignatureHeader] = signature ?? ComputeSignature(bodyBytes);
        return context;
    }

    private static WebhookEndpointOptions CreateEndpointOptions()
    {
        return new WebhookEndpointOptions(ProviderName);
    }

    private static void ConfigureProvider(WebhookKitOptions options)
    {
        options.MaxRequestBodySizeBytes = 1024;
        options.AddProvider(ProviderName, provider =>
        {
            provider.Signature.HeaderName = SignatureHeader;
            provider.Signature.Secret = Secret;
            provider.Timestamp.HeaderName = TimestampHeader;
            provider.EventIdHeaderName = EventIdHeader;
            provider.EventTypeHeaderName = EventTypeHeader;
        });
    }

    private static HttpRequestMessage CreateRequest(
        string path,
        string? eventId,
        string? eventType,
        string? signature = null,
        string body = "{\"value\":42}",
        DateTimeOffset? timestamp = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };
        if (eventId is not null)
        {
            request.Headers.TryAddWithoutValidation(EventIdHeader, eventId);
        }

        if (eventType is not null)
        {
            request.Headers.TryAddWithoutValidation(EventTypeHeader, eventType);
        }

        request.Headers.TryAddWithoutValidation(TimestampHeader, (timestamp ?? FixedNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(SignatureHeader, signature ?? ComputeSignature(bodyBytes));
        return request;
    }

    private static string ComputeSignature(byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private sealed class ResponseTestHost : IDisposable
    {
        public ResponseTestHost(TestServer server)
        {
            Server = server;
            Client = server.CreateClient();
        }

        public TestServer Server { get; }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    private sealed class FixedFormatter : IWebhookResponseFormatter
    {
        public WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null)
        {
            return new WebhookResponseProblem("custom-code", "Custom safe message.", traceId);
        }
    }

    private sealed class ThrowingDispatchProcessor : IWebhookDispatchProcessor
    {
        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("exception-message-secret");
        }
    }

    private sealed class ResponseQueue : IWebhookQueue
    {
        public bool EnqueueResult { get; init; } = true;

        public List<WebhookWorkItem> Items { get; } = [];

        public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnqueueResult)
            {
                Items.Add(workItem);
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

    private sealed class ResponseHandler : IWebhookHandler<ResponsePayload>
    {
        public Task HandleAsync(ResponsePayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            _ = context.GetPayload<ResponsePayload>();
            return Task.CompletedTask;
        }
    }

    private sealed class BrokenResponseHandler : IWebhookHandler<BrokenPayload>
    {
        public Task HandleAsync(BrokenPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            _ = context.GetPayload<BrokenPayload>();
            return Task.CompletedTask;
        }
    }

    private sealed record ResponsePayload(int Value);

    private sealed record BrokenPayload(int RequiredValue);
}

[ApiController]
[Route("mvc-response")]
public sealed class ResponseController : ControllerBase
{
    [HttpPost("valid")]
    [WebhookEndpoint("response-provider")]
    public IActionResult Valid()
    {
        return Ok();
    }

    [HttpPost("error")]
    [WebhookEndpoint("response-provider")]
    public IActionResult Error()
    {
        return Ok();
    }
}
