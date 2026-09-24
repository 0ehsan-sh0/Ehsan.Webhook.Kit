using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.DependencyInjection;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Processing;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

public sealed class WebhookEndpointLoggingTests
{
    private const int CompletedEventId = 1810;
    private const int RejectedEventId = 1811;
    private const int FailedEventId = 1812;
    private const string ProviderName = "endpoint-logging-provider";
    private const string EventIdHeader = "X-Event-Id";
    private const string EventTypeHeader = "X-Event-Type";
    private const string SignatureHeader = "X-Signature";
    private const string TimestampHeader = "X-Timestamp";
    private const string BodySecret = "endpoint-raw-body-secret-marker";
    private const string HeaderSecret = "endpoint-authorization-secret-marker";
    private const string SignatureSecret = "endpoint-signature-secret-marker";
    private const string VerifierSecret = "endpoint-verifier-reason-secret-marker";
    private const string HandlerSecret = "endpoint-handler-exception-secret-marker";
    private const string FixedWebhookId = "endpoint-logging-webhook-id";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("processed", WebhookEndpointOutcome.Processed, 200, CompletedEventId, LogLevel.Information, "processed", "Synchronous", true, null)]
    [InlineData("accepted", WebhookEndpointOutcome.Accepted, 202, CompletedEventId, LogLevel.Information, "accepted", "Asynchronous", true, null)]
    [InlineData("ignored", WebhookEndpointOutcome.Ignored, 200, CompletedEventId, LogLevel.Information, "ignored", "Synchronous", true, null)]
    [InlineData("invalid-signature", WebhookEndpointOutcome.InvalidSignature, 401, RejectedEventId, LogLevel.Warning, "signature-verification-failed", "Synchronous", true, "signature-verification-failed")]
    [InlineData("invalid-timestamp", WebhookEndpointOutcome.InvalidTimestamp, 400, RejectedEventId, LogLevel.Warning, "timestamp-verification-failed", "Synchronous", true, "timestamp-verification-failed")]
    [InlineData("missing-event-id", WebhookEndpointOutcome.MissingEventId, 400, RejectedEventId, LogLevel.Warning, "event-id-required", "Synchronous", true, "event-id-required")]
    [InlineData("missing-event-type", WebhookEndpointOutcome.MissingEventType, 400, RejectedEventId, LogLevel.Warning, "missing-event-type", "Synchronous", true, "missing-event-type")]
    [InlineData("payload-invalid", WebhookEndpointOutcome.PayloadInvalid, 400, RejectedEventId, LogLevel.Warning, "payload-invalid", "Synchronous", true, "payload-invalid")]
    [InlineData("payload-too-large", WebhookEndpointOutcome.PayloadTooLarge, 413, RejectedEventId, LogLevel.Warning, "payload-too-large", "Synchronous", false, "payload-too-large")]
    [InlineData("queue-unavailable", WebhookEndpointOutcome.QueueUnavailable, 503, RejectedEventId, LogLevel.Warning, "queue-unavailable", "Asynchronous", true, "queue-unavailable")]
    [InlineData("processing-failed", WebhookEndpointOutcome.ProcessingFailed, 500, FailedEventId, LogLevel.Error, "handler-failed", "Synchronous", true, "handler-failed")]
    [InlineData("configuration-error", WebhookEndpointOutcome.ConfigurationError, 500, FailedEventId, LogLevel.Error, "webhook-configuration-error", "Synchronous", false, "webhook-configuration-error")]
    public async Task ProcessAsync_LogsEachEndpointOutcomeWithFixedStructuredDefinition(
        string scenario,
        WebhookEndpointOutcome expectedOutcome,
        int expectedStatus,
        int expectedEventId,
        LogLevel expectedLevel,
        string expectedCode,
        string expectedMode,
        bool hasWebhookId,
        string? expectedFailureCode)
    {
        var logs = new CapturingLoggerProvider();
        var result = await RunScenarioAsync(scenario, logs);
        var entry = EndpointEntries(logs).Should().ContainSingle().Subject;

        result.Outcome.Should().Be(expectedOutcome);
        result.StatusCode.Should().Be(expectedStatus);
        result.Code.Should().Be(expectedCode);
        entry.EventId.Id.Should().Be(expectedEventId);
        entry.Level.Should().Be(expectedLevel);
        entry.Template.Should().Be(expectedLevel switch
        {
            LogLevel.Information => "Webhook endpoint completed. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} FailureCode={FailureCode}",
            LogLevel.Warning => "Webhook endpoint rejected. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} FailureCode={FailureCode}",
            _ => "Webhook endpoint failed. Provider={Provider} WebhookId={WebhookId} EventId={EventId} EventType={EventType} Status={Status} Outcome={Outcome} Mode={Mode} TraceId={TraceId} FailureCode={FailureCode}"
        });
        entry.Get("Provider").Should().Be(scenario == "configuration-error" ? "unknown-endpoint-provider" : ProviderName);
        entry.Get("WebhookId").Should().Be(hasWebhookId ? FixedWebhookId : null);
        entry.Get("Status").Should().Be(expectedStatus);
        entry.Get("Outcome").Should().Be(expectedOutcome.ToString());
        entry.Get("Mode").Should().Be(expectedMode);
        entry.Get("TraceId").Should().Be("endpoint-host-trace");
        entry.Get("FailureCode").Should().Be(expectedFailureCode);
        entry.Exception.Should().BeNull();
        AssertSafe(entry);
    }

    [Fact]
    public async Task ProcessAsync_LogsDuplicateWithSafeIdentifierAndNoReexecution()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var options = CreateOptions();
        var firstContext = CreateRequest("evt-duplicate-logging", "known.event");
        var secondContext = CreateRequest("evt-duplicate-logging", "known.event");

        var first = await service.ProcessAsync(firstContext, options);
        var second = await service.ProcessAsync(secondContext, options);

        first.Outcome.Should().Be(WebhookEndpointOutcome.Processed);
        second.Outcome.Should().Be(WebhookEndpointOutcome.Duplicate);
        var duplicate = EndpointEntries(logs).Single(entry => entry.Get("Outcome")?.ToString() == nameof(WebhookEndpointOutcome.Duplicate));
        duplicate.EventId.Id.Should().Be(CompletedEventId);
        duplicate.Level.Should().Be(LogLevel.Information);
        duplicate.Get("WebhookId").Should().Be(FixedWebhookId);
        duplicate.Get("Status").Should().Be(200);
        duplicate.Get("Outcome").Should().Be(nameof(WebhookEndpointOutcome.Duplicate));
        duplicate.Get("Mode").Should().Be(nameof(WebhookProcessingMode.Synchronous));
        duplicate.Get("FailureCode").Should().BeNull();
        AssertSafe(duplicate);
    }

    [Fact]
    public async Task ProcessAsync_LogsBeforeResponseWriterFormatsProblem()
    {
        var logs = new CapturingLoggerProvider();
        var formatter = new OrderingFormatter(logs);
        using var provider = BuildProvider(
            logs,
            signatureVerifier: new RejectingSignatureVerifier(VerifierSecret),
            responseFormatter: formatter);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var context = CreateRequest("evt-order-logging", "known.event", signature: SignatureSecret);

        await service.ProcessAsync(context, CreateOptions());

        formatter.ObservedEventIds.Should().Contain(RejectedEventId);
        formatter.ObservedEventIds.Should().NotContain(CompletedEventId);
        formatter.ObservedEventIds.IndexOf(RejectedEventId).Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_UsesActivityTraceIdWithoutReplacingItWithWebhookId()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var activity = new Activity("endpoint-logging").Start();
        var context = CreateRequest("evt-activity-logging", "known.event", traceIdentifier: string.Empty);

        try
        {
            await service.ProcessAsync(context, CreateOptions());

            var entry = EndpointEntries(logs).Single();
            entry.Get("TraceId").Should().Be(activity.TraceId.ToString());
            entry.Get("TraceId").Should().NotBe(FixedWebhookId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task ProcessAsync_UsesHostTraceIdentifierAndDoesNotFallbackToWebhookId()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        var context = CreateRequest("evt-host-trace-logging", "known.event", traceIdentifier: "host-trace-value");

        try
        {
            await service.ProcessAsync(context, CreateOptions());

            var entry = EndpointEntries(logs).Single();
            entry.Get("TraceId").Should().Be("host-trace-value");
            entry.Get("TraceId").Should().NotBe(FixedWebhookId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task ProcessAsync_UsesNullTraceWhenNeitherActivityNorHostValueExists()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        var context = CreateRequest("evt-no-trace-logging", "known.event", traceIdentifier: string.Empty);

        try
        {
            await service.ProcessAsync(context, CreateOptions());

            var entry = EndpointEntries(logs).Single();
            entry.Get("TraceId").Should().BeNull();
            entry.Get("TraceId").Should().NotBe(FixedWebhookId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task ProcessAsync_ExceptionAndInputMarkersNeverReachStructuredState()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(
            logs,
            processor: new ThrowingDispatchProcessor(HandlerSecret),
            signatureVerifier: new ValidSignatureVerifier());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var context = CreateRequest("evt-sensitive-logging", "known.event");

        await service.ProcessAsync(context, CreateOptions());

        var entry = EndpointEntries(logs).Single();
        entry.Exception.Should().BeNull();
        var state = string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));
        var text = $"{entry.Template}|{entry.Formatted}|{state}";
        text.Should().NotContain(BodySecret);
        text.Should().NotContain(HeaderSecret);
        text.Should().NotContain(SignatureSecret);
        text.Should().NotContain(VerifierSecret);
        text.Should().NotContain(HandlerSecret);
    }

    private static async Task<WebhookEndpointResult> RunScenarioAsync(string scenario, CapturingLoggerProvider logs)
    {
        var mode = scenario is "accepted" or "queue-unavailable"
            ? WebhookProcessingMode.Asynchronous
            : WebhookProcessingMode.Synchronous;
        var providerName = scenario == "configuration-error" ? "unknown-endpoint-provider" : ProviderName;
        IWebhookSignatureVerifier signatureVerifier = scenario == "invalid-signature"
            ? new RejectingSignatureVerifier(VerifierSecret)
            : new ValidSignatureVerifier();
        IWebhookTimestampVerifier timestampVerifier = scenario == "invalid-timestamp"
            ? new RejectingTimestampVerifier(VerifierSecret)
            : new ValidTimestampVerifier();
        IWebhookDispatchProcessor? processor = scenario switch
        {
            "payload-invalid" => new PayloadFailingDispatchProcessor(),
            "processing-failed" => new ThrowingDispatchProcessor(HandlerSecret),
            _ => null
        };
        var queue = scenario == "accepted" ? new RecordingQueue() : null;
        var bodyLimit = scenario == "payload-too-large" ? 4 : 1024;
        var eventId = scenario is "missing-event-id" ? null : $"evt-{scenario}";
        var eventType = scenario switch
        {
            "missing-event-type" => null,
            "ignored" => "unknown.event",
            _ => "known.event"
        };
        var body = scenario == "payload-too-large"
            ? new string('x', 128)
            : $"{{\"secret\":\"{BodySecret}\",\"value\":42}}";
        using var provider = BuildProvider(
            logs,
            signatureVerifier: signatureVerifier,
            timestampVerifier: timestampVerifier,
            processor: processor,
            queue: queue,
            bodyLimit: bodyLimit);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IWebhookEndpointService>();
        var context = CreateRequest(eventId, eventType, body: Encoding.UTF8.GetBytes(body), signature: SignatureSecret, providerName: providerName);

        return await service.ProcessAsync(context, new WebhookEndpointOptions(providerName) { Mode = mode });
    }

    private static ServiceProvider BuildProvider(
        CapturingLoggerProvider logs,
        IWebhookSignatureVerifier? signatureVerifier = null,
        IWebhookTimestampVerifier? timestampVerifier = null,
        IWebhookDispatchProcessor? processor = null,
        IWebhookQueue? queue = null,
        IWebhookResponseFormatter? responseFormatter = null,
        long bodyLimit = 1024)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<WebhookEndpointService>>(logs.CreateLogger<WebhookEndpointService>());
        services.AddWebhookKit(options =>
        {
            options.MaxRequestBodySizeBytes = bodyLimit;
            options.AddProvider(ProviderName, provider =>
            {
                provider.MaxRequestBodySizeBytes = bodyLimit;
                provider.Signature.HeaderName = SignatureHeader;
                provider.Signature.Secret = SignatureSecret;
                provider.Timestamp.HeaderName = TimestampHeader;
                provider.EventIdHeaderName = EventIdHeader;
                provider.EventTypeHeaderName = EventTypeHeader;
            });
        });
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        services.AddSingleton<IWebhookIdGenerator, FixedIdGenerator>();
        services.AddWebhookKitAspNetCore();
        services.AddSingleton(signatureVerifier ?? new ValidSignatureVerifier());
        services.AddSingleton(timestampVerifier ?? new ValidTimestampVerifier());
        if (processor is not null)
        {
            services.AddSingleton(processor);
        }

        if (queue is not null)
        {
            services.AddSingleton(queue);
        }

        if (responseFormatter is not null)
        {
            services.AddSingleton(responseFormatter);
        }

        services.AddWebhookHandler<EndpointLoggingHandler>("known.event");
        return services.BuildServiceProvider();
    }

    private static WebhookEndpointOptions CreateOptions()
    {
        return new WebhookEndpointOptions(ProviderName);
    }

    private static DefaultHttpContext CreateRequest(
        string? eventId,
        string? eventType,
        byte[]? body = null,
        string? signature = SignatureSecret,
        string providerName = ProviderName,
        string traceIdentifier = "endpoint-host-trace")
    {
        body ??= Encoding.UTF8.GetBytes($"{{\"secret\":\"{BodySecret}\",\"value\":42}}");
        var context = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/webhooks/endpoint-logging";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.Request.Headers[TimestampHeader] = FixedNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Request.Headers[SignatureHeader] = signature ?? SignatureSecret;
        context.Request.Headers["X-Secret-Header"] = HeaderSecret;
        if (eventId is not null)
        {
            context.Request.Headers[EventIdHeader] = eventId;
        }

        if (eventType is not null)
        {
            context.Request.Headers[EventTypeHeader] = eventType;
        }

        return context;
    }

    private static CapturedEndpointLog[] EndpointEntries(CapturingLoggerProvider logs)
    {
        return logs.Entries
            .Where(entry => entry.Category == typeof(WebhookEndpointService).FullName)
            .Select(entry => new CapturedEndpointLog(entry))
            .ToArray();
    }

    private static void AssertSafe(CapturedEndpointLog entry)
    {
        entry.Exception.Should().BeNull();
        var state = string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));
        var text = $"{entry.Template}|{entry.Formatted}|{state}";
        text.Should().NotContain(BodySecret);
        text.Should().NotContain(HeaderSecret);
        text.Should().NotContain(SignatureSecret);
        text.Should().NotContain(VerifierSecret);
        text.Should().NotContain(HandlerSecret);
    }

    private sealed class FixedIdGenerator : IWebhookIdGenerator
    {
        public string Create()
        {
            return FixedWebhookId;
        }
    }

    private sealed class ValidSignatureVerifier : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }
    }

    private sealed class RejectingSignatureVerifier(string reason) : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Fail(reason));
        }
    }

    private sealed class ValidTimestampVerifier : IWebhookTimestampVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Success());
        }
    }

    private sealed class RejectingTimestampVerifier(string reason) : IWebhookTimestampVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WebhookVerificationResult.Fail(reason));
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

    private sealed class ThrowingDispatchProcessor(string message) : IWebhookDispatchProcessor
    {
        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingQueue : IWebhookQueue
    {
        public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    public sealed class EndpointLoggingHandler : IWebhookHandler<EndpointPayload>
    {
        public Task HandleAsync(EndpointPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed record EndpointPayload(int Value);

    public sealed class CapturedEndpointLog
    {
        public CapturedEndpointLog(CapturedLog log)
        {
            Log = log;
        }

        public CapturedLog Log { get; }
        public string Category => Log.Category;
        public LogLevel Level => Log.Level;
        public EventId EventId => Log.EventId;
        public string Template => Log.Template;
        public string Formatted => Log.Formatted;
        public IReadOnlyList<KeyValuePair<string, object?>> State => Log.State;
        public Exception? Exception => Log.Exception;

        public object? Get(string name)
        {
            return Log.Get(name);
        }
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

        public ILogger<T> CreateLogger<T>()
        {
            return new CapturingLogger<T>(this, typeof(T).FullName ?? typeof(T).Name);
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(this, categoryName);
        }

        public void Add(
            string category,
            LogLevel level,
            EventId eventId,
            string template,
            string formatted,
            IReadOnlyList<KeyValuePair<string, object?>> state,
            Exception? exception)
        {
            _entries.Enqueue(new CapturedLog(category, level, eventId, template, formatted, state, exception));
        }

        public void Dispose()
        {
        }
    }

    public sealed class CapturedLog
    {
        public CapturedLog(
            string category,
            LogLevel level,
            EventId eventId,
            string template,
            string formatted,
            IReadOnlyList<KeyValuePair<string, object?>> state,
            Exception? exception)
        {
            Category = category;
            Level = level;
            EventId = eventId;
            Template = template;
            Formatted = formatted;
            State = state;
            Exception = exception;
        }

        public string Category { get; }
        public LogLevel Level { get; }
        public EventId EventId { get; }
        public string Template { get; }
        public string Formatted { get; }
        public IReadOnlyList<KeyValuePair<string, object?>> State { get; }
        public Exception? Exception { get; }

        public object? Get(string name)
        {
            return State.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;
        }
    }

    public class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return new Scope();
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var snapshot = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToArray()
                : Array.Empty<KeyValuePair<string, object?>>();
            var formatted = formatter(state, exception);
            var template = snapshot
                .FirstOrDefault(pair => string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                .Value as string ?? formatted;
            provider.Add(category, logLevel, eventId, template, formatted, snapshot, exception);
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    public sealed class CapturingLogger<T>(CapturingLoggerProvider provider, string category)
        : CapturingLogger(provider, category), ILogger<T>;

    private sealed class OrderingFormatter(CapturingLoggerProvider logs) : IWebhookResponseFormatter
    {
        public List<int> ObservedEventIds { get; } = new();

        public WebhookResponseProblem Format(WebhookEndpointOutcome outcome, string? traceId = null)
        {
            ObservedEventIds.AddRange(logs.Entries.Select(entry => entry.EventId.Id));
            return new WebhookResponseProblem("safe-ordering-problem", "Safe ordering problem.");
        }
    }
}
