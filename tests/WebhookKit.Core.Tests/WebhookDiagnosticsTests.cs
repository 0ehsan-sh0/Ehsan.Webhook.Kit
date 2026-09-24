using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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

[CollectionDefinition("WebhookDiagnostics", DisableParallelization = true)]
public sealed class WebhookDiagnosticsTestGroup
{
}

[Collection("WebhookDiagnostics")]
public sealed class WebhookDiagnosticsTests
{
    private const string ProviderName = "diagnostics-provider";
    private const string EventType = "diagnostics.event";
    private const string EventId = "evt-diagnostics";
    private const string WebhookId = "diagnostics-webhook-id";
    private const string BodySecret = "diagnostics-raw-body-secret-marker";
    private const string HeaderSecret = "diagnostics-authorization-header-secret-marker";
    private const string SignatureSecret = "diagnostics-signature-secret-marker";
    private const string VerifierSecret = "diagnostics-verifier-reason-secret-marker";
    private const string ParserSecret = "diagnostics-parser-detail-secret-marker";
    private const string HandlerSecret = "diagnostics-handler-exception-secret-marker";
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_UsesRequiredNameAndVersion()
    {
        var source = FindSource();

        source.Should().NotBeNull();
        source!.Name.Should().Be("WebhookKit");
        source.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task IngestAsync_CreatesReceiveAndProcessActivitiesWithSafeTagsAndParenting()
    {
        var activities = new List<Activity>();
        using var listener = StartListener(activities);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var parent = new Activity("host").SetIdFormat(ActivityIdFormat.W3C).Start();

        try
        {
            var result = await service.IngestAsync(CreateRequest());

            result.Status.Should().Be(WebhookIngestionStatus.Processed);
            activities.Should().HaveCount(2);
            activities.Select(activity => activity.OperationName).Should().BeEquivalentTo(
                "WebhookKit.Receive",
                "WebhookKit.Process");
            var receive = activities.Single(activity => activity.OperationName == "WebhookKit.Receive");
            var process = activities.Single(activity => activity.OperationName == "WebhookKit.Process");
            receive.ParentSpanId.Should().Be(parent.SpanId);
            process.ParentSpanId.Should().Be(receive.SpanId);
            receive.TraceId.Should().Be(parent.TraceId);
            process.TraceId.Should().Be(parent.TraceId);
            AssertRequiredTags(receive, "Processed");
            AssertRequiredTags(process, "Processed");
            receive.GetTagItem("correlation.id").Should().NotBeNull();
            process.GetTagItem("correlation.id").Should().Be(receive.GetTagItem("correlation.id"));
            receive.Status.Should().Be(ActivityStatusCode.Ok);
            process.Status.Should().Be(ActivityStatusCode.Ok);
            activities.Should().OnlyContain(activity => activity.Events.Count() == 0);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task IngestionRequest_WithoutCorrelation_UsesStableNonNullFallback()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var request = CreateRequest();

        var result = await service.IngestAsync(request);

        result.Record!.CorrelationId.Should().NotBeNullOrWhiteSpace();
        result.Context!.CorrelationId.Should().Be(result.Record.CorrelationId);
    }

    [Fact]
    public async Task IngestAsync_PersistsDistinctGeneratedCorrelationAndUpdatesExtractedMetadata()
    {
        var activities = new List<Activity>();
        using var listener = StartListener(activities);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var store = scope.ServiceProvider.GetRequiredService<IWebhookStore>();

        var result = await service.IngestAsync(CreateRequest());

        result.Status.Should().Be(WebhookIngestionStatus.Processed);
        var recordCorrelationId = ReadCorrelationId(result.Record!);
        var contextCorrelationId = ReadCorrelationId(result.Context!);
        var stored = await store.GetByWebhookIdAsync(WebhookId);
        recordCorrelationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(recordCorrelationId, out _).Should().BeTrue();
        recordCorrelationId.Should().NotBe(WebhookId);
        recordCorrelationId.Should().NotBe(EventId);
        contextCorrelationId.Should().Be(recordCorrelationId);
        ReadCorrelationId(stored!).Should().Be(recordCorrelationId);
        activities.Should().OnlyContain(activity =>
            Equals(activity.GetTagItem("webhook.event_id"), EventId) &&
            Equals(activity.GetTagItem("webhook.event_type"), EventType) &&
            Equals(activity.GetTagItem("webhook.status"), "Processed"));
    }

    [Fact]
    public async Task IngestAsync_PrefersExplicitCorrelationAndDoesNotAliasWebhookOrEventId()
    {
        var activities = new List<Activity>();
        using var listener = StartListener(activities);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var request = CreateRequest();
        SetCorrelationId(request, "explicit-correlation-id");

        var result = await service.IngestAsync(request);

        result.Status.Should().Be(WebhookIngestionStatus.Processed);
        ReadCorrelationId(result.Record!).Should().Be("explicit-correlation-id");
        ReadCorrelationId(result.Context!).Should().Be("explicit-correlation-id");
        activities.Should().OnlyContain(activity => Equals(activity.GetTagItem("correlation.id"), "explicit-correlation-id"));
    }

    [Fact]
    public async Task IngestAsync_PrefersAmbientActivityTraceWhenRequestCorrelationIsMissing()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var parent = new Activity("ambient-host").SetIdFormat(ActivityIdFormat.W3C).Start();

        try
        {
            var result = await service.IngestAsync(CreateRequest());

            result.Status.Should().Be(WebhookIngestionStatus.Processed);
            ReadCorrelationId(result.Record!).Should().Be(parent.TraceId.ToString());
            ReadCorrelationId(result.Context!).Should().Be(parent.TraceId.ToString());
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task IngestAsync_ReplacesCorrelationWhenExplicitValueAliasesWebhookOrEventId()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var request = CreateRequest();
        SetCorrelationId(request, EventId);

        var result = await service.IngestAsync(request);

        result.Status.Should().Be(WebhookIngestionStatus.Processed);
        var correlationId = ReadCorrelationId(result.Record!);
        correlationId.Should().NotBeNullOrWhiteSpace();
        correlationId.Should().NotBe(EventId);
        correlationId.Should().NotBe(WebhookId);
        Guid.TryParse(correlationId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IngestAsync_LogsCorrelationAlongsideSafeTraceFields()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildProvider(logs: logs);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        var previousActivity = Activity.Current;
        Activity.Current = null;
        using var activity = new Activity("logging-parent").SetIdFormat(ActivityIdFormat.W3C).Start();

        try
        {
            var result = await service.IngestAsync(CreateRequest());

            result.Status.Should().Be(WebhookIngestionStatus.Processed);
            var correlationId = ReadCorrelationId(result.Record!);
            correlationId.Should().NotBeNullOrWhiteSpace();
            logs.Entries.Should().Contain(entry => Equals(entry.Get("CorrelationId"), correlationId));
            logs.Entries.Should().OnlyContain(entry => entry.Exception == null);
            AssertLogsAreSafe(logs.Entries);
        }
        finally
        {
            Activity.Current = previousActivity;
        }
    }

    [Fact]
    public async Task IngestAsync_FailedHandlerProducesErrorActivitiesWithoutSensitiveValues()
    {
        var activities = new List<Activity>();
        using var listener = StartListener(activities);
        using var provider = BuildProvider(failingHandler: true);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();

        var result = await service.IngestAsync(CreateRequest());

        result.Status.Should().Be(WebhookIngestionStatus.Failed);
        activities.Should().HaveCount(2);
        activities.Should().OnlyContain(activity => activity.Status == ActivityStatusCode.Error);
        activities.Should().OnlyContain(activity => activity.Events.Count() == 0);
        AssertActivitiesAreSafe(activities);
    }

    [Fact]
    public async Task IngestAsync_CancellationProducesErrorActivitiesWithoutExceptionValues()
    {
        var activities = new List<Activity>();
        using var listener = StartListener(activities);
        var probe = new CancellationProbe();
        using var provider = BuildProvider(cancellationProbe: probe);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WebhookIngestionService>();
        using var cancellation = new CancellationTokenSource();
        var ingestion = service.IngestAsync(CreateRequest(), cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var act = async () => await ingestion;

        await act.Should().ThrowAsync<OperationCanceledException>();
        activities.Should().HaveCount(2);
        activities.Should().OnlyContain(activity => activity.Status == ActivityStatusCode.Error);
        activities.Should().OnlyContain(activity => activity.Events.Count() == 0);
        AssertActivitiesAreSafe(activities);
    }

    private static ActivitySource? FindSource()
    {
        var type = typeof(WebhookIngestionService).Assembly.GetType("WebhookKit.Core.Diagnostics.WebhookDiagnostics");
        return type?.GetProperty("Source", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ActivitySource;
    }

    private static ActivityListener StartListener(List<Activity> activities)
    {
        var source = FindSource() ?? new ActivitySource("WebhookKit", "1.0.0");
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == source.Name && candidate.Version == source.Version,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertRequiredTags(Activity activity, string status)
    {
        activity.GetTagItem("webhook.id").Should().Be(WebhookId);
        activity.GetTagItem("webhook.provider").Should().Be(ProviderName);
        activity.GetTagItem("webhook.event_id").Should().Be(EventId);
        activity.GetTagItem("webhook.event_type").Should().Be(EventType);
        activity.GetTagItem("webhook.status").Should().Be(status);
    }

    private static void AssertActivitiesAreSafe(IEnumerable<Activity> activities)
    {
        foreach (var activity in activities)
        {
            var text = string.Join(
                "|",
                activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"),
                activity.DisplayName,
                activity.ToString(),
                activity.Events.SelectMany(activityEvent => activityEvent.Tags.Select(tag => $"{tag.Key}={tag.Value}")));
            text.Should().NotContain(BodySecret);
            text.Should().NotContain(HeaderSecret);
            text.Should().NotContain(SignatureSecret);
            text.Should().NotContain(VerifierSecret);
            text.Should().NotContain(ParserSecret);
            text.Should().NotContain(HandlerSecret);
        }
    }

    private static void AssertLogsAreSafe(IEnumerable<CapturedLog> entries)
    {
        foreach (var entry in entries)
        {
            var text = $"{entry.Template}|{entry.Formatted}|{string.Join("|", entry.State.Select(pair => $"{pair.Key}={pair.Value}"))}";
            text.Should().NotContain(BodySecret);
            text.Should().NotContain(HeaderSecret);
            text.Should().NotContain(SignatureSecret);
            text.Should().NotContain(VerifierSecret);
            text.Should().NotContain(ParserSecret);
            text.Should().NotContain(HandlerSecret);
        }
    }

    private static string? ReadCorrelationId(object value)
    {
        return value.GetType().GetProperty("CorrelationId", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as string;
    }

    private static void SetCorrelationId(WebhookIngestionRequest request, string correlationId)
    {
        var property = typeof(WebhookIngestionRequest).GetProperty("CorrelationId", BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull();
        property!.SetValue(request, correlationId);
    }

    private static WebhookIngestionRequest CreateRequest()
    {
        var body = $"{{\"secret\":\"{BodySecret}\",\"value\":42}}";
        return new WebhookIngestionRequest
        {
            WebhookId = WebhookId,
            Provider = ProviderName,
            HttpMethod = "POST",
            RequestPath = "/webhooks/diagnostics",
            Headers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Event-Id"] = [EventId],
                ["X-Event-Type"] = [EventType],
                ["X-Authorization"] = [HeaderSecret],
                ["X-Signature"] = [SignatureSecret]
            },
            RawBody = Encoding.UTF8.GetBytes(body),
            ContentType = "application/json",
            ContentLength = Encoding.UTF8.GetByteCount(body)
        };
    }

    private static ServiceProvider BuildProvider(
        bool failingHandler = false,
        CancellationProbe? cancellationProbe = null,
        CapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookSignatureVerifier, ValidSignatureVerifier>();
        services.AddSingleton<IWebhookTimestampVerifier, ValidTimestampVerifier>();
        services.AddSingleton<IWebhookEventIdExtractor, FixedEventIdExtractor>();
        services.AddSingleton<IWebhookEventTypeExtractor, FixedEventTypeExtractor>();
        services.AddWebhookKit(options => options.AddProvider(ProviderName, provider =>
        {
            provider.AllowBodyHashFallback = true;
            provider.Timestamp.AllowMissing = true;
        }));
        services.AddSingleton<IWebhookClock>(new FakeWebhookClock(FixedNow));
        if (logs is not null)
        {
            services.AddSingleton<ILogger<WebhookIngestionService>>(logs.CreateLogger<WebhookIngestionService>());
            services.AddSingleton<ILogger<WebhookProcessor>>(logs.CreateLogger<WebhookProcessor>());
        }

        if (cancellationProbe is not null)
        {
            services.AddSingleton(cancellationProbe);
            services.AddWebhookHandler<CancellationHandler>(EventType);
        }
        else if (failingHandler)
        {
            services.AddWebhookHandler<FailingHandler>(EventType);
        }
        else
        {
            services.AddWebhookHandler<SuccessHandler>(EventType);
        }

        return services.BuildServiceProvider();
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

    private sealed class FixedEventIdExtractor : IWebhookEventIdExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(EventId);
        }
    }

    private sealed class FixedEventTypeExtractor : IWebhookEventTypeExtractor
    {
        public ValueTask<string?> ExtractAsync(
            WebhookVerificationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(EventType);
        }
    }

    public sealed record Payload(int Value);

    public sealed class SuccessHandler : IWebhookHandler<Payload>
    {
        public Task HandleAsync(Payload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class FailingHandler : IWebhookHandler<Payload>
    {
        public Task HandleAsync(Payload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(HandlerSecret);
        }
    }

    public sealed class CancellationProbe
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class CancellationHandler(CancellationProbe probe) : IWebhookHandler<Payload>
    {
        public async Task HandleAsync(Payload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    public sealed class CapturedLog
    {
        public CapturedLog(string template, string formatted, IReadOnlyList<KeyValuePair<string, object?>> state, Exception? exception)
        {
            Template = template;
            Formatted = formatted;
            State = state;
            Exception = exception;
        }

        public string Template { get; }
        public string Formatted { get; }
        public IReadOnlyList<KeyValuePair<string, object?>> State { get; }
        public Exception? Exception { get; }

        public object? Get(string name)
        {
            return State.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;
        }
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

        public ILogger<T> CreateLogger<T>() => new CapturingLogger<T>(this);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Add(CapturedLog entry) => _entries.Enqueue(entry);

        public void Dispose()
        {
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => new Scope();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToArray()
                    : Array.Empty<KeyValuePair<string, object?>>();
                provider.Add(new CapturedLog(
                    values.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string ?? formatter(state, exception),
                    formatter(state, exception),
                    values,
                    exception));
            }
        }

        private sealed class CapturingLogger<T>(CapturingLoggerProvider provider) : CapturingLogger(provider), ILogger<T>;
    }
}
