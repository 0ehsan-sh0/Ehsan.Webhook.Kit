using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Retries;
using Xunit;

namespace WebhookKit.Core.Tests;

public sealed class WebhookRetryTests
{
    [Fact]
    public void RetryOptions_DefaultToThreeAttemptsTwoSecondInitialDelayMultiplierTwoAndJitter()
    {
        var options = new WebhookRetryOptions();

        options.MaxAttempts.Should().Be(3);
        options.InitialDelay.Should().Be(TimeSpan.FromSeconds(2));
        options.BackoffMultiplier.Should().Be(2);
        options.UseJitter.Should().BeTrue();
        options.JitterRatio.Should().Be(0.2);
    }

    [Fact]
    public void Classifier_OnlyExplicitRetryableExceptionIsRetryable()
    {
        WebhookRetryClassifier.Classify(new WebhookRetryableException()).Should().Be(WebhookRetryClassification.Retryable);
        WebhookRetryClassifier.Classify(new WebhookPermanentException()).Should().Be(WebhookRetryClassification.Permanent);
        WebhookRetryClassifier.Classify(new WebhookPayloadException()).Should().Be(WebhookRetryClassification.Payload);
        WebhookRetryClassifier.Classify(new InvalidOperationException()).Should().Be(WebhookRetryClassification.Unknown);
        WebhookRetryClassifier.Classify(new OperationCanceledException()).Should().Be(WebhookRetryClassification.Cancellation);
        WebhookRetryClassifier.IsRetryable(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public async Task Executor_RetriesRealProcessorResultRetainedAsExplicitRetryable()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RetryProbe>();
        services.AddWebhookKit();
        services.AddWebhookHandler<RetryThenSuccessHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IWebhookRetryExecutor>();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var probe = scope.ServiceProvider.GetRequiredService<RetryProbe>();
        var options = new WebhookRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.Zero,
            BackoffMultiplier = 2,
            UseJitter = false
        };

        var result = await executor.ExecuteAsync(processor, CreateContext(), options);

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        probe.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Executor_NormalizesRealProcessorPermanentExceptionToSafeCode()
    {
        var services = new ServiceCollection();
        services.AddWebhookKit();
        services.AddWebhookHandler<PermanentFailingHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IWebhookRetryExecutor>();
        var processor = scope.ServiceProvider.GetRequiredService<IWebhookDispatchProcessor>();
        var options = new WebhookRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.Zero,
            BackoffMultiplier = 2,
            UseJitter = false
        };

        var result = await executor.ExecuteAsync(processor, CreateContext(), options);

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureCode.Should().Be("permanent-failure");
    }

    [Fact]
    public void Policy_CalculatesExactExponentialSequenceWithoutJitter()
    {
        var policy = new WebhookRetryPolicy(() => 0);
        var options = new WebhookRetryOptions
        {
            MaxAttempts = 4,
            InitialDelay = TimeSpan.FromMilliseconds(10),
            BackoffMultiplier = 2,
            UseJitter = false
        };

        policy.CalculateDelay(1, options).Should().Be(TimeSpan.FromMilliseconds(10));
        policy.CalculateDelay(2, options).Should().Be(TimeSpan.FromMilliseconds(20));
        policy.CalculateDelay(3, options).Should().Be(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public void Policy_BoundsAdditiveJitterAndNeverReturnsNegativeDelay()
    {
        var policy = new WebhookRetryPolicy(() => 0.5);
        var options = new WebhookRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = 2,
            UseJitter = true,
            JitterRatio = 0.25
        };

        policy.CalculateDelay(1, options, 0).Should().Be(TimeSpan.FromSeconds(2));
        policy.CalculateDelay(1, options, 0.5).Should().Be(TimeSpan.FromSeconds(2.25));
        policy.CalculateDelay(1, options, 1).Should().Be(TimeSpan.FromSeconds(2.5));
        policy.CalculateDelay(2, options, 0).Should().Be(TimeSpan.FromSeconds(4));
        policy.CalculateDelay(2, options, 1).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Policy_MaximumRetryWindowIncludesMaximumJitter()
    {
        var policy = new WebhookRetryPolicy(() => 1);
        var options = new WebhookRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = 2,
            UseJitter = true,
            JitterRatio = 0.5
        };

        WebhookRetryPolicy.GetMaximumRetryWindow(options).Should().Be(TimeSpan.FromSeconds(9));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public async Task Executor_StopsAfterFirstSecondOrThirdSuccessfulAttempt(int successAttempt, int expectedFailures)
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((attempt, _) =>
        {
            if (attempt < successAttempt)
            {
                throw new WebhookRetryableException();
            }

            return Task.FromResult(WebhookDispatchResult.Processed());
        });
        var executor = CreateExecutor(processor, delay);
        var options = CreateOptions();

        var result = await executor.ExecuteAsync(processor, CreateContext(), options);

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        processor.Calls.Should().Be(successAttempt);
        delay.Delays.Should().HaveCount(expectedFailures);
    }

    [Fact]
    public async Task Executor_RetriesOnlyExplicitRetryableException()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => throw new WebhookRetryableException("sensitive retry detail"));
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(processor, CreateContext(), CreateOptions());

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureCode.Should().Be("retryable-failure");
        result.ToString().Should().NotContain("sensitive retry detail");
        processor.Calls.Should().Be(3);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData(typeof(WebhookPermanentException), "permanent-failure")]
    [InlineData(typeof(WebhookPayloadException), "payload-invalid")]
    [InlineData(typeof(InvalidOperationException), "handler-failed")]
    public async Task Executor_TerminatesNonRetryableFailuresWithoutDelay(Type exceptionType, string expectedCode)
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => throw CreateException(exceptionType));
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(processor, CreateContext(), CreateOptions());

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureCode.Should().Be(expectedCode);
        processor.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_TerminatesWhenDispatchReturnsMissingFailureException()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => Task.FromResult(
            WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed")));
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(processor, CreateContext(), CreateOptions());

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureCode.Should().Be("handler-failed");
        processor.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_TerminatesWhenDispatchReturnsNoResult()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => Task.FromResult<WebhookDispatchResult>(null!));
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(processor, CreateContext(), CreateOptions());

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureCode.Should().Be("dispatch-result-missing");
        processor.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_PersistsEveryActualAttemptBeforeRetrying()
    {
        var persistedAttempts = new List<int>();
        var retryEvents = new List<(int Attempt, TimeSpan Delay, string Code)>();
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((attempt, _) =>
        {
            if (attempt < 3)
            {
                throw new WebhookRetryableException();
            }

            return Task.FromResult(WebhookDispatchResult.Processed());
        });
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(
            processor,
            CreateContext(),
            CreateOptions(),
            (attempt, _) =>
            {
                persistedAttempts.Add(attempt);
                return Task.CompletedTask;
            },
            (attempt, nextDelay, code, _) =>
            {
                retryEvents.Add((attempt, nextDelay, code));
                return Task.CompletedTask;
            });

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        persistedAttempts.Should().Equal(1, 2, 3);
        retryEvents.Should().HaveCount(2);
        retryEvents.Select(item => item.Attempt).Should().Equal(1, 2);
        retryEvents.Select(item => item.Code).Should().Equal("retryable-failure", "retryable-failure");
    }

    [Fact]
    public async Task Executor_DoesNotWaitProductionScaleDelaysInTests()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => Task.FromResult(WebhookDispatchResult.Processed()));
        var executor = CreateExecutor(processor, delay);
        var options = CreateOptions();

        var result = await executor.ExecuteAsync(processor, CreateContext(), options);

        result.Status.Should().Be(WebhookDispatchStatus.Processed);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_PropagatesCancellationDuringDispatchWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor(async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return WebhookDispatchResult.Processed();
        });
        var executor = CreateExecutor(processor, delay);
        var operation = executor.ExecuteAsync(processor, CreateContext(), CreateOptions(), cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        var act = async () => await operation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        processor.Calls.Should().Be(1);
        delay.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Executor_PropagatesCancellationDuringDelayImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        var delay = new RecordingDelay(block: true);
        var processor = new ScriptedDispatchProcessor((_, _) => throw new WebhookRetryableException());
        var executor = CreateExecutor(processor, delay);
        var operation = executor.ExecuteAsync(processor, CreateContext(), CreateOptions(), cancellationToken: cancellation.Token);
        await delay.Entered.Task;

        cancellation.Cancel();
        var act = async () => await operation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        processor.Calls.Should().Be(1);
        delay.Delays.Should().ContainSingle();
    }

    [Fact]
    public async Task Executor_ContinuesFromPersistedAttemptNumberWithoutExceedingTotalAttempts()
    {
        var persistedAttempts = new List<int>();
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => throw new WebhookRetryableException());
        var executor = CreateExecutor(processor, delay);
        var options = CreateOptions();

        var result = await executor.ExecuteAsync(
            processor,
            CreateContext(),
            options,
            (attempt, _) =>
            {
                persistedAttempts.Add(attempt);
                return Task.CompletedTask;
            },
            firstAttempt: 2);

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        processor.Calls.Should().Be(2);
        persistedAttempts.Should().Equal(2, 3);
        delay.Delays.Should().ContainSingle();
    }

    [Fact]
    public async Task Executor_UsesConfiguredMaximumAttempts()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => throw new WebhookRetryableException());
        var executor = CreateExecutor(processor, delay);
        var options = CreateOptions();
        options.MaxAttempts = 2;

        var result = await executor.ExecuteAsync(processor, CreateContext(), options);

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        processor.Calls.Should().Be(2);
        delay.Delays.Should().ContainSingle();
    }

    [Fact]
    public async Task Executor_ReturnsSafeTerminalFailureAfterExhaustion()
    {
        var delay = new RecordingDelay();
        var processor = new ScriptedDispatchProcessor((_, _) => throw new WebhookRetryableException("do not expose this message"));
        var executor = CreateExecutor(processor, delay);

        var result = await executor.ExecuteAsync(processor, CreateContext(), CreateOptions());

        result.Status.Should().Be(WebhookDispatchStatus.Failed);
        result.FailureKind.Should().Be(WebhookDispatchFailureKind.Handler);
        result.FailureCode.Should().Be("retryable-failure");
        result.ToString().Should().NotContain("do not expose this message");
        processor.Calls.Should().Be(3);
    }

    private static WebhookRetryExecutor CreateExecutor(
        IWebhookDispatchProcessor processor,
        RecordingDelay delay)
    {
        return new WebhookRetryExecutor(new WebhookRetryPolicy(() => 0), delay);
    }

    private static WebhookRetryOptions CreateOptions()
    {
        return new WebhookRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromSeconds(2),
            BackoffMultiplier = 2,
            UseJitter = false
        };
    }

    private static WebhookContext CreateContext()
    {
        return new WebhookContext(new byte[] { 1 }, new StringDeserializer())
        {
            WebhookId = "webhook-retry",
            Provider = "test",
            EventId = "event-retry",
            EventType = "event.type",
            ReceivedAt = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
            Headers = new Dictionary<string, IReadOnlyList<string>>()
        };
    }

    private static Exception CreateException(Type exceptionType)
    {
        return exceptionType == typeof(WebhookPermanentException)
            ? new WebhookPermanentException()
            : exceptionType == typeof(WebhookPayloadException)
                ? new WebhookPayloadException()
                : new InvalidOperationException();
    }

    private sealed class StringDeserializer : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            return (T)(object)"test";
        }
    }

    public sealed class RetryProbe
    {
        public int Calls { get; set; }
    }

    public sealed class RetryThenSuccessHandler(RetryProbe probe) : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            probe.Calls++;
            if (probe.Calls < 3)
            {
                throw new WebhookRetryableException();
            }

            return Task.CompletedTask;
        }
    }

    public sealed class PermanentFailingHandler : IWebhookHandler<string>
    {
        public Task HandleAsync(string eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            throw new WebhookPermanentException();
        }
    }

    private sealed class ScriptedDispatchProcessor(
        Func<int, CancellationToken, Task<WebhookDispatchResult>> behavior) : IWebhookDispatchProcessor
    {
        private int _calls;

        public int Calls => _calls;

        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            _calls++;
            return behavior(_calls, cancellationToken);
        }
    }

    private sealed class RecordingDelay(bool block = false) : IWebhookRetryDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Entered.TrySetResult(true);
            if (block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }
}
