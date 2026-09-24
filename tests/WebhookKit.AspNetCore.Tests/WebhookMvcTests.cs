using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Mvc;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.Clocks;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

public sealed class WebhookMvcTests
{
    private const string ProviderName = "mvc-provider";
    private const string KnownEventType = "known.event";
    private const string SignatureHeader = "X-Signature";
    private const string TimestampHeader = "X-Timestamp";
    private const string EventIdHeader = "X-Event-Id";
    private const string EventTypeHeader = "X-Event-Type";
    private const string Secret = "mvc-secret-value";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mvc_ValidRequest_ExecutesActionAndBindsSafeContext()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/valid", "evt-valid", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = host.Probe.Calls.Should().ContainSingle().Which;
        call.Context.Should().NotBeNull();
        call.Context!.EventId.Should().Be("evt-valid");
        call.Context.EventType.Should().Be(KnownEventType);
        call.Context.GetPayload<MvcPayload>().Value.Should().Be(42);
    }

    [Fact]
    public async Task Mvc_InvalidSignature_ShortCircuitsActionAndPreservesUnauthorizedStatus()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/invalid", "evt-invalid", KnownEventType, "invalid-signature"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mvc_Duplicate_ShortCircuitsSecondAction()
    {
        using var host = CreateHost();
        using var first = await host.Client.SendAsync(CreateRequest("/mvc/duplicate", "evt-duplicate", KnownEventType));
        using var second = await host.Client.SendAsync(CreateRequest("/mvc/duplicate", "evt-duplicate", KnownEventType));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Mvc_UnknownEvent_ShortCircuitsAction()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/ignored", "evt-ignored", "unknown.event"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mvc_AsyncAccepted_ShortCircuitsByDefault()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/async-default", "evt-async-default", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.Probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mvc_AsyncAccepted_ExplicitPolicyExecutesActionAndBindsContext()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/async-opt-in", "evt-async-opt-in", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var call = host.Probe.Calls.Should().ContainSingle().Which;
        call.Context.Should().NotBeNull();
        call.Context!.EventId.Should().Be("evt-async-opt-in");
    }

    [Fact]
    public async Task Mvc_HandledDuplicateFlag_AllowsNullableContextAndPassesNull()
    {
        using var host = CreateHost();
        using var first = await host.Client.SendAsync(CreateRequest("/mvc/handled-duplicate", "evt-handled-duplicate", KnownEventType));
        using var second = await host.Client.SendAsync(CreateRequest("/mvc/handled-duplicate", "evt-handled-duplicate", KnownEventType));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Calls.Should().HaveCount(2);
        host.Probe.Calls[0].Context.Should().NotBeNull();
        host.Probe.Calls[1].Context.Should().BeNull();
    }

    [Fact]
    public async Task Mvc_HandledIgnoredFlag_ExecutesOnlyForIgnoredOutcome()
    {
        using var host = CreateHost();
        using var processed = await host.Client.SendAsync(CreateRequest("/mvc/handled-ignored", "evt-handled-processed", KnownEventType));
        using var ignored = await host.Client.SendAsync(CreateRequest("/mvc/handled-ignored", "evt-handled-ignored", "unknown.event"));

        processed.StatusCode.Should().Be(HttpStatusCode.OK);
        ignored.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = host.Probe.Calls.Should().ContainSingle().Which;
        call.Action.Should().Be("handled-ignored");
        call.Context.Should().NotBeNull();
    }

    [Fact]
    public async Task Mvc_RequiredContextWithHandledOutcomeWithoutContext_ReturnsSafeConfigurationFailure()
    {
        using var host = CreateHost();
        using var first = await host.Client.SendAsync(CreateRequest("/mvc/required-duplicate", "evt-required-duplicate", KnownEventType));
        using var second = await host.Client.SendAsync(CreateRequest("/mvc/required-duplicate", "evt-required-duplicate", KnownEventType));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Probe.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Mvc_Cancellation_PropagatesAndDoesNotExecuteAction()
    {
        var reader = new BlockingBodyReader();
        using var host = CreateHost(bodyReader: reader);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var send = host.Client.SendAsync(CreateRequest("/mvc/valid", "evt-cancelled", KnownEventType), cancellation.Token);

        await reader.Entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        var act = async () => await send;

        await act.Should().ThrowAsync<TaskCanceledException>();
        host.Probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mvc_InvalidProvider_ReturnsSafeConfigurationFailure()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/invalid-provider", "evt-invalid-provider", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.Probe.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mvc_ActionWithoutAttribute_IsUntouched()
    {
        using var host = CreateHost();
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/ordinary", "evt-ordinary", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Probe.Calls.Should().ContainSingle();
        host.Probe.Calls[0].Action.Should().Be("ordinary");
    }

    [Fact]
    public async Task Mvc_GlobalFilter_PreservesApplicationFilter()
    {
        using var host = CreateHost(registerApplicationFilter: true);
        using var response = await host.Client.SendAsync(CreateRequest("/mvc/valid", "evt-filter-compatibility", KnownEventType));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ApplicationFilterCalls.Calls.Should().Be(1);
        host.Probe.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Mvc_ResponseCodesMatchMinimalEndpoint()
    {
        using var host = CreateHost();
        var cases = new[]
        {
            (Path: "/mvc/valid", MinimalPath: "/minimal", EventId: "evt-parity-valid", EventType: KnownEventType, Signature: (string?)null, Body: "{}"),
            (Path: "/mvc/invalid", MinimalPath: "/minimal", EventId: "evt-parity-invalid", EventType: KnownEventType, Signature: (string?)"invalid-signature", Body: "{}"),
            (Path: "/mvc/ignored", MinimalPath: "/minimal", EventId: "evt-parity-ignored", EventType: "unknown.event", Signature: (string?)null, Body: "{}")
        };

        foreach (var testCase in cases)
        {
            using var mvc = await host.Client.SendAsync(CreateRequest(testCase.Path, testCase.EventId, testCase.EventType, testCase.Signature, testCase.Body));
            using var minimal = await host.Client.SendAsync(CreateRequest(testCase.MinimalPath, testCase.EventId + "-minimal", testCase.EventType, testCase.Signature, testCase.Body));
            mvc.StatusCode.Should().Be(minimal.StatusCode);
        }

        using var mvcLarge = await host.Client.SendAsync(CreateRequest("/mvc/valid", "evt-parity-large", KnownEventType, body: new string('x', 2048)));
        using var minimalLarge = await host.Client.SendAsync(CreateRequest("/minimal", "evt-parity-large-minimal", KnownEventType, body: new string('x', 2048)));
        mvcLarge.StatusCode.Should().Be(minimalLarge.StatusCode);
        mvcLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Mvc_DuplicateAndQueueFailureStatusCodesMatchMinimalEndpoint()
    {
        using var host = CreateHost(includeQueue: false);
        using var firstMinimal = await host.Client.SendAsync(CreateRequest("/minimal", "evt-parity-duplicate", KnownEventType));
        using var secondMvc = await host.Client.SendAsync(CreateRequest("/mvc/duplicate", "evt-parity-duplicate", KnownEventType));
        firstMinimal.StatusCode.Should().Be(HttpStatusCode.OK);
        secondMvc.StatusCode.Should().Be(firstMinimal.StatusCode);

        using var asyncMvc = await host.Client.SendAsync(CreateRequest("/mvc/async-default", "evt-parity-queue", KnownEventType));
        using var asyncMinimal = await host.Client.SendAsync(CreateRequest("/minimal-async", "evt-parity-queue-minimal", KnownEventType));
        asyncMvc.StatusCode.Should().Be(asyncMinimal.StatusCode);
        asyncMvc.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private static MvcTestHost CreateHost(
        IWebhookBodyReader? bodyReader = null,
        bool includeQueue = true,
        bool registerApplicationFilter = false)
    {
        var probe = new MvcActionProbe();
        var applicationFilterCalls = new ApplicationFilterProbe();
        var builder = new WebHostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(probe);
                services.AddSingleton(applicationFilterCalls);
                services.AddWebhookKit(options =>
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
                });
                services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
                services.AddWebhookHandler<MvcRecordingHandler>(KnownEventType);
                services.AddSingleton<MvcRecordingHandler>();
                if (bodyReader is not null)
                {
                    services.AddSingleton<IWebhookBodyReader>(bodyReader);
                }

                if (includeQueue)
                {
                    services.AddSingleton<MvcRecordingSink>();
                    services.AddSingleton<IWebhookQueue>(sp => sp.GetRequiredService<MvcRecordingSink>());
                }
                else
                {
                    services.RemoveAll<IWebhookQueue>();
                }

                services.AddWebhookKitAspNetCore();
                services.AddControllers().AddApplicationPart(typeof(MvcTestController).Assembly);
                if (registerApplicationFilter)
                {
                    services.Configure<MvcOptions>(options => options.Filters.Add<MvcApplicationFilter>());
                }
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapWebhook("/minimal", ProviderName);
                    endpoints.MapWebhook("/minimal-async", new WebhookEndpointOptions(ProviderName)
                    {
                        Mode = WebhookProcessingMode.Asynchronous
                    });
                });
            });

        var server = new TestServer(builder);
        return new MvcTestHost(server, probe, applicationFilterCalls);
    }

    private static HttpRequestMessage CreateRequest(
        string path,
        string eventId,
        string eventType,
        string? signature = null,
        string body = "{\"value\":42}")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation(EventIdHeader, eventId);
        request.Headers.TryAddWithoutValidation(EventTypeHeader, eventType);
        request.Headers.TryAddWithoutValidation(TimestampHeader, FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(SignatureHeader, signature ?? ComputeSignature(bodyBytes));
        return request;
    }

    private static string ComputeSignature(byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private sealed class MvcTestHost : IDisposable
    {
        public MvcTestHost(TestServer server, MvcActionProbe probe, ApplicationFilterProbe applicationFilterCalls)
        {
            Server = server;
            Probe = probe;
            ApplicationFilterCalls = applicationFilterCalls;
            Client = server.CreateClient();
        }

        public TestServer Server { get; }

        public HttpClient Client { get; }

        public MvcActionProbe Probe { get; }

        public ApplicationFilterProbe ApplicationFilterCalls { get; }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}

public sealed class MvcActionProbe
{
    private readonly object _gate = new();
    private readonly List<MvcActionCall> _calls = [];

    public IReadOnlyList<MvcActionCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public void Record(string action, WebhookContext? context)
    {
        lock (_gate)
        {
            _calls.Add(new MvcActionCall(action, context));
        }
    }
}

public sealed record MvcActionCall(string Action, WebhookContext? Context);

public sealed class ApplicationFilterProbe
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Record()
    {
        Interlocked.Increment(ref _calls);
    }
}

public sealed class MvcApplicationFilter : IActionFilter
{
    private readonly ApplicationFilterProbe _probe;

    public MvcApplicationFilter(ApplicationFilterProbe probe)
    {
        _probe = probe;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        _probe.Record();
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

public sealed class BlockingBodyReader : IWebhookBodyReader
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<byte[]> ReadRawBodyAsync(HttpContext context, long maxSizeBytes, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return [];
    }
}

public sealed class MvcRecordingHandler : IWebhookHandler<MvcPayload>
{
    public Task HandleAsync(MvcPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
    {
        _ = context.GetPayload<MvcPayload>();
        return Task.CompletedTask;
    }
}

public sealed class MvcRecordingSink : IWebhookQueue
{
    public List<WebhookWorkItem> Items { get; } = [];

    public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
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

[ApiController]
[Route("mvc")]
public sealed class MvcTestController : ControllerBase
{
    private readonly MvcActionProbe _probe;

    public MvcTestController(MvcActionProbe probe)
    {
        _probe = probe;
    }

    [HttpPost("valid")]
    [WebhookEndpoint("mvc-provider")]
    public IActionResult Valid(WebhookContext context)
    {
        _probe.Record("valid", context);
        return Ok();
    }

    [HttpPost("invalid")]
    [WebhookEndpoint("mvc-provider")]
    public IActionResult Invalid(WebhookContext context)
    {
        _probe.Record("invalid", context);
        return Ok();
    }

    [HttpPost("duplicate")]
    [WebhookEndpoint("mvc-provider")]
    public IActionResult Duplicate(WebhookContext context)
    {
        _probe.Record("duplicate", context);
        return Ok();
    }

    [HttpPost("ignored")]
    [WebhookEndpoint("mvc-provider")]
    public IActionResult Ignored(WebhookContext context)
    {
        _probe.Record("ignored", context);
        return Ok();
    }

    [HttpPost("async-default")]
    [WebhookEndpoint("mvc-provider", Mode = WebhookProcessingMode.Asynchronous)]
    public IActionResult AsyncDefault(WebhookContext context)
    {
        _probe.Record("async-default", context);
        return Ok();
    }

    [HttpPost("async-opt-in")]
    [WebhookEndpoint(
        "mvc-provider",
        Mode = WebhookProcessingMode.Asynchronous,
        ActionPolicy = WebhookEndpointActionPolicy.Processed | WebhookEndpointActionPolicy.Accepted)]
    public IActionResult AsyncOptIn(WebhookContext context)
    {
        _probe.Record("async-opt-in", context);
        return StatusCode(StatusCodes.Status202Accepted);
    }

    [HttpPost("handled-duplicate")]
    [WebhookEndpoint(
        "mvc-provider",
        ActionPolicy = WebhookEndpointActionPolicy.Processed | WebhookEndpointActionPolicy.Duplicate)]
    public IActionResult HandledDuplicate(WebhookContext? context)
    {
        _probe.Record("handled-duplicate", context);
        return Ok();
    }

    [HttpPost("handled-ignored")]
    [WebhookEndpoint("mvc-provider", ActionPolicy = WebhookEndpointActionPolicy.Ignored)]
    public IActionResult HandledIgnored(WebhookContext? context)
    {
        _probe.Record("handled-ignored", context);
        return Ok();
    }

    [HttpPost("required-duplicate")]
    [WebhookEndpoint(
        "mvc-provider",
        ActionPolicy = WebhookEndpointActionPolicy.Processed | WebhookEndpointActionPolicy.Duplicate)]
    public IActionResult RequiredDuplicate(WebhookContext context)
    {
        _probe.Record("required-duplicate", context);
        return Ok();
    }

    [HttpPost("invalid-provider")]
    [WebhookEndpoint("   ")]
    public IActionResult InvalidProvider(WebhookContext context)
    {
        _probe.Record("invalid-provider", context);
        return Ok();
    }

    [HttpPost("ordinary")]
    public IActionResult Ordinary()
    {
        _probe.Record("ordinary", null);
        return Ok();
    }
}

public sealed record MvcPayload(int Value);
