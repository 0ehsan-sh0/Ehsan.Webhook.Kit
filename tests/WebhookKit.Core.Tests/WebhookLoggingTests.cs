using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookKit.Abstractions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Processing;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookLoggingTests
{
    private const int ReceivedEventId = 1801;
    private const int VerifiedEventId = 1802;
    private const int RejectedEventId = 1803;
    private const int DuplicateEventId = 1804;
    private const int IgnoredEventId = 1805;
    private const int ProcessingEventId = 1806;
    private const int ProcessedEventId = 1807;
    private const int FailedEventId = 1808;
    private const int RetryEventId = 1809;
    private const string ProviderName = "logging-provider";
    private const string BodySecret = "raw-body-secret-marker";
    private const string HeaderSecret = "authorization-header-secret-marker";
    private const string SignatureSecret = "signature-secret-marker";
    private const string VerifierReasonSecret = "verifier-reason-secret-marker";
    private const string ParserSecret = "parser-detail-secret-marker";
    private const string HandlerSecret = "handler-exception-secret-marker";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IngestionService_LogsSafeStructuredSuccessOutcomeWithFixedDefinitions()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var activity = new Activity("webhook-logging").Start();

        try
        {
            var result = await service.IngestAsync(CreateRequest("webhook-logging-success"));

            result.Status.Should().Be(WebhookIngestionStatus.Processed);
            var entries = CoreEntries(logs);
            entries.Select(entry => entry.EventId.Id).Should().Equal(
                ReceivedEventId,
                VerifiedEventId,
                ProcessingEventId,
                ProcessedEventId);
            entries.Select(entry => entry.Level).Should().Equal(
                LogLevel.Information,
                LogLevel.Information,
                LogLevel.Debug,
                LogLevel.Information);
            entries.Select(entry => entry.Template).Should().Equal(
                "Webhook received. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}",
                "Webhook verified. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}",
                "Webhook processing. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}",
                "Webhook processed. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}");
            AssertFields(entries[0], "webhook-logging-success", null, null, "Received", activity.TraceId.ToString(), null);
            AssertFields(entries[1], "webhook-logging-success", null, null, "Verified", activity.TraceId.ToString(), null);
            AssertFields(entries[2], "webhook-logging-success", "evt-logging", "event.type", "Processing", activity.TraceId.ToString(), null);
            AssertFields(entries[3], "webhook-logging-success", "evt-logging", "event.type", "Processed", activity.TraceId.ToString(), null);
            entries.Should().OnlyContain(entry => entry.Exception == null);
            AssertSafe(entries, BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
            entries.Select(entry => entry.EventId.Id).Should().NotContain(RetryEventId);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task IngestionService_LogsRejectedOutcomeWithoutVerifierReason()
    {
        var logs = new CapturingLoggerProvider();
        var signature = new ResultSignatureVerifier(WebhookVerificationResult.Fail(VerifierReasonSecret));
        using var provider = BuildProvider(
            logs,
            signatureVerifier: signature,
            eventIdExtractor: new ThrowingEventIdExtractor(ParserSecret));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var activity = new Activity("webhook-rejection").Start();

        try
        {
            var result = await service.IngestAsync(CreateRequest("webhook-logging-rejected", eventId: "evt-rejected"));

            result.Status.Should().Be(WebhookIngestionStatus.Rejected);
            var entries = CoreEntries(logs);
            entries.Select(entry => entry.EventId.Id).Should().Equal(ReceivedEventId, RejectedEventId);
            entries[1].Level.Should().Be(LogLevel.Warning);
            entries[1].Template.Should().Be("Webhook rejected. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}");
            AssertFields(entries[1], "webhook-logging-rejected", null, null, "Rejected", activity.TraceId.ToString(), "signature-verification-failed");
            entries[1].Has("FailureReason").Should().BeFalse();
            entries.Should().OnlyContain(entry => entry.Exception == null);
            AssertSafe(entries, BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task IngestionService_LogsDuplicateWithExtractedMetadata()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var first = await service.IngestAsync(CreateRequest("webhook-logging-first"));
        var second = await service.IngestAsync(CreateRequest("webhook-logging-duplicate"));

        first.Status.Should().Be(WebhookIngestionStatus.Processed);
        second.Status.Should().Be(WebhookIngestionStatus.Duplicate);
        var duplicate = CoreEntries(logs).Single(entry => entry.EventId.Id == DuplicateEventId);
        AssertFields(duplicate, "webhook-logging-duplicate", "evt-logging", "event.type", "Duplicate", null, null);
        duplicate.Has("FailureCode").Should().BeFalse();
        AssertSafe(CoreEntries(logs), BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
    }

    [Fact]
    public async Task IngestionService_LogsIgnoredOutcomeWhenNoHandlerMatches()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(
            logs,
            eventTypeExtractor: new FixedEventTypeExtractor("unregistered.event"),
            registerHandler: false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateRequest("webhook-logging-ignored", eventType: "unregistered.event"));

        result.Status.Should().Be(WebhookIngestionStatus.Ignored);
        var ignored = CoreEntries(logs).Single(entry => entry.EventId.Id == IgnoredEventId);
        AssertFields(ignored, "webhook-logging-ignored", "evt-logging", "unregistered.event", "Ignored", null, null);
        ignored.Level.Should().Be(LogLevel.Information);
        ignored.Template.Should().Be("Webhook ignored. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId}");
        AssertSafe(CoreEntries(logs), BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
    }

    [Fact]
    public async Task IngestionService_LogsHandlerFailureWithOnlySafeFailureCode()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs, failingHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateRequest("webhook-logging-failed"));

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        var failed = CoreEntries(logs).Single(entry => entry.EventId.Id == FailedEventId);
        AssertFields(failed, "webhook-logging-failed", "evt-logging", "event.type", "Failed", null, "handler-failed");
        failed.Level.Should().Be(LogLevel.Error);
        failed.Template.Should().Be("Webhook failed. WebhookId={WebhookId} Provider={Provider} EventId={EventId} EventType={EventType} Status={Status} Attempt={Attempt} TraceId={TraceId} CorrelationId={CorrelationId} FailureCode={FailureCode}");
        failed.Has("Exception").Should().BeFalse();
        failed.Exception.Should().BeNull();
        AssertSafe(CoreEntries(logs), BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
    }

    [Fact]
    public async Task IngestionService_LogsExtractionFailureWithOnlySafeFailureCode()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs, eventIdExtractor: new ThrowingEventIdExtractor(ParserSecret));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateRequest("webhook-logging-extraction-failed"));

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        var failed = CoreEntries(logs).Single(entry => entry.EventId.Id == FailedEventId);
        AssertFields(failed, "webhook-logging-extraction-failed", null, null, "Failed", null, "event-id-extraction-failed");
        AssertSafe(CoreEntries(logs), BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
    }

    [Fact]
    public async Task IngestionService_LogsFailedOutcomeWhenVerificationThrowsWithoutExceptionValue()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(
            logs,
            signatureVerifier: new ThrowingSignatureVerifier(HandlerSecret));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateRequest("webhook-logging-verification-failed"));

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        var failed = CoreEntries(logs).Single(entry => entry.EventId.Id == FailedEventId);
        AssertFields(failed, "webhook-logging-verification-failed", null, null, "Failed", null, "signature-verification-failed");
        failed.Exception.Should().BeNull();
        AssertSafe(CoreEntries(logs), BodySecret, HeaderSecret, SignatureSecret, VerifierReasonSecret, ParserSecret, HandlerSecret);
    }

    [Fact]
    public async Task IngestionService_CancellationDoesNotCreateFailureLog()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => service.IngestAsync(CreateRequest("webhook-logging-cancelled"), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        CoreEntries(logs).Should().BeEmpty();
    }

    [Fact]
    public async Task IngestionService_UsesActivityTraceIdAndDoesNotFallbackToWebhookId()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;

        try
        {
            await service.IngestAsync(CreateRequest("webhook-logging-no-trace"));
            var received = CoreEntries(logs).Single(entry => entry.EventId.Id == ReceivedEventId);
            received.Get("TraceId").Should().BeNull();
            received.Get("WebhookId").Should().Be("webhook-logging-no-trace");
            received.Get("TraceId").Should().NotBe("webhook-logging-no-trace");
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    private static void AssertFields(
        CapturedLog entry,
        string webhookId,
        string? eventId,
        string? eventType,
        string status,
        string? traceId,
        string? failureCode)
    {
        entry.Get("WebhookId").Should().Be(webhookId);
        entry.Get("Provider").Should().Be(ProviderName);
        entry.Get("EventId").Should().Be(eventId);
        entry.Get("EventType").Should().Be(eventType);
        entry.Get("Status").Should().Be(status);
        entry.Get("Attempt").Should().Be(0);
        entry.Get("TraceId").Should().Be(traceId);
        var correlationId = entry.Get("CorrelationId") as string;
        correlationId.Should().NotBeNullOrWhiteSpace();
        correlationId.Should().NotBe(webhookId);
        if (failureCode is null)
        {
            entry.Has("FailureCode").Should().BeFalse();
        }
        else
        {
            entry.Get("FailureCode").Should().Be(failureCode);
        }
    }

    private static void AssertSafe(IEnumerable<CapturedLog> entries, params string[] markers)
    {
        foreach (var entry in entries)
        {
            entry.Exception.Should().BeNull();
            var values = string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}"));
            var text = $"{entry.Template}|{entry.Formatted}|{values}";
            foreach (var marker in markers)
            {
                text.Should().NotContain(marker);
            }
        }
    }

    private static CapturedLog[] CoreEntries(CapturingLoggerProvider logs)
    {
        return logs.Entries
            .Where(entry => entry.Category == typeof(WebhookIngestionService).FullName || entry.Category == typeof(WebhookProcessor).FullName)
            .ToArray();
    }

    private static ServiceProvider BuildProvider(
        CapturingLoggerProvider logs,
        IWebhookSignatureVerifier? signatureVerifier = null,
        IWebhookEventIdExtractor? eventIdExtractor = null,
        IWebhookEventTypeExtractor? eventTypeExtractor = null,
        IWebhookDispatchProcessor? processor = null,
        bool registerHandler = true,
        bool failingHandler = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<WebhookIngestionService>>(logs.CreateLogger<WebhookIngestionService>());
        services.AddSingleton<ILogger<WebhookProcessor>>(logs.CreateLogger<WebhookProcessor>());
        services.AddSingleton(signatureVerifier ?? new ValidSignatureVerifier());
        services.AddSingleton<IWebhookTimestampVerifier, ValidTimestampVerifier>();
        services.AddSingleton(eventIdExtractor ?? new FixedEventIdExtractor("evt-logging"));
        services.AddSingleton<IWebhookEventTypeExtractor>(eventTypeExtractor ?? new FixedEventTypeExtractor("event.type"));
        services.AddWebhookKit(options => options.AddProvider(ProviderName, provider =>
        {
            provider.AllowBodyHashFallback = true;
            provider.Timestamp.AllowMissing = true;
        }));
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        if (processor is not null)
        {
            services.AddSingleton(processor);
        }

        if (registerHandler)
        {
            if (failingHandler)
            {
                services.AddWebhookHandler<ThrowingHandler>("event.type");
            }
            else
            {
                services.AddWebhookHandler<LoggingHandler>("event.type");
            }
        }

        return services.BuildServiceProvider();
    }

    private static WebhookIngestionRequest CreateRequest(
        string webhookId,
        string? eventId = "evt-logging",
        string? eventType = "event.type")
    {
        var body = $"{{\"secret\":\"{BodySecret}\",\"value\":42}}";
        return new WebhookIngestionRequest
        {
            WebhookId = webhookId,
            Provider = ProviderName,
            HttpMethod = "POST",
            RequestPath = "/webhooks/logging",
            Headers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Event-Id"] = eventId is null ? [] : [eventId],
                ["X-Event-Type"] = eventType is null ? [] : [eventType],
                ["X-Secret-Header"] = [HeaderSecret],
                ["X-Signature"] = [SignatureSecret]
            },
            RawBody = Encoding.UTF8.GetBytes(body),
            ContentType = "application/json",
            ContentLength = Encoding.UTF8.GetByteCount(body)
        };
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

    private sealed class ResultSignatureVerifier(WebhookVerificationResult result) : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingSignatureVerifier(string message) : IWebhookSignatureVerifier
    {
        public ValueTask<WebhookVerificationResult> VerifyAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
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

    private sealed class FixedEventIdExtractor(string value) : IWebhookEventIdExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(value);
        }
    }

    private sealed class ThrowingEventIdExtractor(string message) : IWebhookEventIdExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FixedEventTypeExtractor(string value) : IWebhookEventTypeExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(value);
        }
    }

    public sealed class LoggingHandler : IWebhookHandler<LoggingPayload>
    {
        public Task HandleAsync(LoggingPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowingHandler : IWebhookHandler<LoggingPayload>
    {
        public Task HandleAsync(LoggingPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(HandlerSecret);
        }
    }

    public sealed record LoggingPayload(int Value);

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

        public bool Has(string name)
        {
            return State.Any(pair => string.Equals(pair.Key, name, StringComparison.Ordinal));
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
}
