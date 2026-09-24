using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebhookKit.Abstractions;
using WebhookKit.Abstractions.Exceptions;
using WebhookKit.Core.DependencyInjection;
using WebhookKit.Core.Options;
using WebhookKit.Core.Processing;
using WebhookKit.Core.Queues;
using WebhookKit.Core.Retries;
using WebhookKit.Core.Stores;
using WebhookKit.Core.Workers;
using WebhookKit.Testing;
using Xunit;

namespace WebhookKit.Core.Tests;

[CollectionDefinition("WebhookBackgroundWorker", DisableParallelization = true)]
public sealed class WebhookBackgroundWorkerTestGroup
{
}

[Collection("WebhookBackgroundWorker")]
public sealed class WebhookBackgroundWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task StartAsync_WithReceivedWorkItem_ClaimsDispatchesAndMarksProcessed()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store);
        var record = CreateRecord("webhook-success");
        await store.Inner.TryCreateAsync(record);
        var dispatched = harness.Probe.FirstDispatch;

        (await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider))).Should().BeTrue();
        var context = await dispatched.Task.WaitAsync(TestTimeout);
        await store.Processed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processed);
        stored.AttemptCount.Should().Be(1);
        stored.ProcessingLeaseOwner.Should().BeNull();
        stored.ProcessingLeaseExpiresAt.Should().BeNull();
        stored.FailureCode.Should().BeNull();
        context.WebhookId.Should().Be(record.Id);
        context.EventId.Should().Be(record.EventId);
        context.EventType.Should().Be(record.EventType);
    }

    [Fact]
    public async Task StartAsync_UsesOneProcessActivityForTheDispatch()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var store = new ObservingStore(new InMemoryWebhookStore(clock));
        var services = new ServiceCollection();
        services.AddSingleton<IWebhookClock>(clock);
        services.AddSingleton<IWebhookStore>(store);
        services.AddWebhookKit(options => options.Background.Enabled = true);
        services.AddWebhookHandler<ActivityHandler>("event.type");
        using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<WebhookBackgroundWorker>().Single();
        var queue = provider.GetRequiredService<IWebhookQueue>();
        var record = CreateRecord("webhook-activity");
        await store.Inner.TryCreateAsync(record);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = StartActivityListener(record.Id, activities);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            (await queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider))).Should().BeTrue();
            await store.Processed.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            await worker.StopAsync(cancellation.Token);
        }

        activities.Should().ContainSingle();
        activities.Single().GetTagItem("correlation.id").Should().Be(record.CorrelationId);
    }

    [Fact]
    public async Task StartAsync_WithUnregisteredEventType_MarksIgnoredWithoutDispatchFailure()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: (_, _) => Task.FromResult(WebhookDispatchResult.Ignored()));
        var record = CreateRecord("webhook-ignored");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));

        await harness.Probe.FirstDispatch.Task.WaitAsync(TestTimeout);
        await store.Ignored.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Ignored);
        stored.FailureCode.Should().BeNull();
        harness.Probe.DispatchCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WithMissingRecord_DoesNotDispatch()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem("missing", "test"));
        await store.Lookup.Task.WaitAsync(TestTimeout);

        harness.Probe.DispatchCount.Should().Be(0);
        (await store.GetByWebhookIdAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WithTerminalRecord_DoesNotDispatchOrReclaim()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store);
        var record = CreateRecord("webhook-terminal", status: WebhookProcessingStatus.Processed);
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Lookup.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processed);
        stored.AttemptCount.Should().Be(0);
        harness.Probe.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WhenClaimConflictsWithActiveLease_SkipsDispatch()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var store = CreateStore(clock);
        var record = CreateRecord("webhook-claim-conflict");
        await store.Inner.TryCreateAsync(record);
        (await store.Inner.TryClaimAsync(record.Id, "other-worker", TimeSpan.FromMinutes(5))).Should().BeTrue();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store, clock: clock);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.ClaimAttempted.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.ProcessingLeaseOwner.Should().Be("other-worker");
        stored.AttemptCount.Should().Be(1);
        harness.Probe.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ReconstructsPersistedContextWithScopedDeserializerAndCorrelation()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: (context, _) =>
        {
            context.GetPayload<WorkerPayload>().Value.Should().Be(42);
            return Task.FromResult(WebhookDispatchResult.Processed());
        });
        var record = CreateRecord("webhook-scoped");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Processed.Task.WaitAsync(TestTimeout);

        var context = harness.Probe.Contexts.Single();
        context.CorrelationId.Should().Be(record.CorrelationId);
        context.Headers["X-Test"].Should().Equal("value");
        harness.Probe.ScopeIds.Should().ContainSingle();
        harness.Probe.DeserializerScopeIds.Should().ContainSingle();
        harness.Probe.ScopeIds.Single().Should().Be(harness.Probe.DeserializerScopeIds.Single());
    }

    [Fact]
    public async Task StartAsync_WhenRawBodyIsMissing_MarksSafeFailureWithoutDispatch()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store);
        var record = CreateRecord("webhook-no-body", includeRawBody: false);
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Failed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Failed);
        stored.FailureCode.Should().Be("raw-body-missing");
        harness.Probe.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WhenDispatchThrowsRetryableException_RetriesAndPersistsSafeExhaustion()
    {
        var store = CreateStore();
        var options = CreateOptions();
        var delay = new RecordingRetryDelay();
        var logs = new CapturingLogger<WebhookBackgroundWorker>();
        await using var harness = await WorkerHarness.StartAsync(
            options,
            store,
            dispatch: (_, _) => throw new WebhookRetryableException("secret retry detail"),
            retryExecutor: new WebhookRetryExecutor(new WebhookRetryPolicy(() => 0), delay),
            logger: logs);
        var record = CreateRecord("webhook-retryable");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Failed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Failed);
        stored.FailureCode.Should().Be("retryable-failure");
        stored.FailureReason.Should().NotContain("secret retry detail");
        stored.AttemptCount.Should().Be(3);
        stored.LastAttemptAt.Should().NotBeNull();
        stored.ProcessingLeaseOwner.Should().BeNull();
        harness.Probe.DispatchCount.Should().Be(3);
        delay.Delays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        logs.Entries.Should().Contain(entry => entry.EventId == 1809 && Equals(entry.Get("Attempt"), 1));
        logs.Entries.Should().Contain(entry => entry.EventId == 1809 && Equals(entry.Get("Attempt"), 2));
        logs.Entries.Should().OnlyContain(entry => entry.Exception == null);
        logs.Entries.Should().OnlyContain(entry => !entry.GetAllText().Contains("secret retry detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_RetryDelayRetainsLeaseOwnerAndAttemptStateUntilTerminalTransition()
    {
        var store = CreateStore();
        var options = CreateOptions();
        var delay = new RecordingRetryDelay(block: true);
        await using var harness = await WorkerHarness.StartAsync(
            options,
            store,
            dispatch: (_, _) => throw new WebhookRetryableException(),
            retryExecutor: new WebhookRetryExecutor(new WebhookRetryPolicy(() => 0), delay));
        var record = CreateRecord("webhook-retry-lease");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await delay.Entered.Task.WaitAsync(TestTimeout);

        var processing = await store.GetByWebhookIdAsync(record.Id);
        processing!.Status.Should().Be(WebhookProcessingStatus.Processing);
        processing.AttemptCount.Should().Be(1);
        processing.LastAttemptAt.Should().NotBeNull();
        processing.ProcessingLeaseOwner.Should().NotBeNullOrWhiteSpace();
        processing.ProcessingLeaseExpiresAt.Should().NotBeNull();

        using var cancellation = new CancellationTokenSource(TestTimeout);
        await harness.Worker.StopAsync(cancellation.Token);
        await store.Released.Task.WaitAsync(TestTimeout);
        var released = await store.GetByWebhookIdAsync(record.Id);
        released!.Status.Should().Be(WebhookProcessingStatus.Received);
        released.ProcessingLeaseOwner.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_WhenDispatchThrowsPermanentException_MarksPermanentTerminalCode()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: (_, _) => throw new WebhookPermanentException());
        var record = CreateRecord("webhook-permanent");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Failed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Failed);
        stored.FailureCode.Should().Be("permanent-failure");
    }

    [Fact]
    public async Task StartAsync_WhenDispatchThrowsUnknownException_MarksSafeHandlerFailure()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: (_, _) => throw new InvalidOperationException("secret detail"));
        var record = CreateRecord("webhook-unknown-failure");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await store.Failed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Failed);
        stored.FailureCode.Should().Be("handler-failed");
        stored.FailureReason.Should().NotContain("secret detail");
    }

    [Fact]
    public async Task StartAsync_RecoversExpiredProcessingLeaseOnTimerButNotActiveLease()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var store = CreateStore(clock);
        var record = CreateRecord("webhook-expired");
        await store.Inner.TryCreateAsync(record);
        (await store.Inner.TryClaimAsync(record.Id, "old-worker", TimeSpan.FromMinutes(5))).Should().BeTrue();
        var options = CreateOptions(recoveryInterval: TimeSpan.FromMilliseconds(20));
        await using var harness = await WorkerHarness.StartAsync(options, store, clock: clock);

        await store.NextRecoveryCall();
        var beforeExpiry = await store.GetByWebhookIdAsync(record.Id);
        beforeExpiry!.Status.Should().Be(WebhookProcessingStatus.Processing);
        beforeExpiry.ProcessingLeaseOwner.Should().Be("old-worker");
        harness.Probe.DispatchCount.Should().Be(0);

        clock.Advance(TimeSpan.FromMinutes(6));
        await store.NextRecoveryCall();
        await store.Processed.Task.WaitAsync(TestTimeout);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processed);
        stored.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task StartAsync_DoesNotRecoverCompletedRecord()
    {
        var store = CreateStore();
        var options = CreateOptions();
        await using var harness = await WorkerHarness.StartAsync(options, store);
        var record = CreateRecord("webhook-completed", status: WebhookProcessingStatus.Processed);
        await store.Inner.TryCreateAsync(record);

        await store.NextRecoveryCall();

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processed);
        harness.Probe.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_RecoversReceivedRecordsAtStartupAndOnTimerWithoutSleeps()
    {
        var store = CreateStore();
        var options = CreateOptions(recoveryInterval: TimeSpan.FromMilliseconds(20));
        await using var harness = await WorkerHarness.StartAsync(options, store);
        await store.NextRecoveryCall();

        var startupRecord = CreateRecord("webhook-startup");
        await store.Inner.TryCreateAsync(startupRecord);
        await store.NextRecoveryCall();
        await store.NextProcessed();

        var timerRecord = CreateRecord("webhook-timer");
        await store.Inner.TryCreateAsync(timerRecord);
        await store.NextRecoveryCall();
        await store.NextProcessed();

        (await store.GetByWebhookIdAsync(startupRecord.Id))!.Status.Should().Be(WebhookProcessingStatus.Processed);
        (await store.GetByWebhookIdAsync(timerRecord.Id))!.Status.Should().Be(WebhookProcessingStatus.Processed);
        harness.Probe.DispatchCount.Should().Be(2);
    }

    [Fact]
    public async Task StartAsync_WhenRecoveryQueueIsFull_LeavesRecordRecoverable()
    {
        var store = CreateStore();
        var queue = new AlwaysFullQueue();
        var options = CreateOptions();
        var record = CreateRecord("webhook-queue-full");
        await store.Inner.TryCreateAsync(record);
        await using var harness = await WorkerHarness.StartAsync(options, store, queue: queue);

        await store.NextRecoveryCall();

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Received);
        stored.AttemptCount.Should().Be(0);
        queue.EnqueueAttempts.Should().Be(1);
        harness.Probe.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithFourWorkers_DispatchesAtMostConfiguredConcurrency()
    {
        const int recordCount = 12;
        var store = CreateStore();
        var options = CreateOptions(workerConcurrency: 4, queueCapacity: recordCount);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedConcurrency = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxActive = 0;
        var startedCount = 0;
        var completedCount = 0;
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, current);
            if (Interlocked.Increment(ref startedCount) == 4)
            {
                reachedConcurrency.TrySetResult(true);
            }

            try
            {
                await release.Task.WaitAsync(token);
                return WebhookDispatchResult.Processed();
            }
            finally
            {
                Interlocked.Decrement(ref active);
                if (Interlocked.Increment(ref completedCount) == recordCount)
                {
                    completed.TrySetResult(true);
                }
            }
        });
        for (var index = 0; index < recordCount; index++)
        {
            var record = CreateRecord($"webhook-concurrent-{index}");
            await store.Inner.TryCreateAsync(record);
            (await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider))).Should().BeTrue();
        }

        await reachedConcurrency.Task.WaitAsync(TestTimeout);
        maxActive.Should().Be(4);
        active.Should().Be(4);
        release.TrySetResult(true);
        await completed.Task.WaitAsync(TestTimeout);
        maxActive.Should().Be(4);
    }

    [Theory]
    [InlineData(WebhookDispatchStatus.Processed)]
    [InlineData(WebhookDispatchStatus.Failed)]
    public async Task StartAsync_WhenLeaseExpiresBeforeOwnedTerminalTransition_LogsStatusUpdateFailureWithoutMutatingReplacementOwner(
        WebhookDispatchStatus dispatchStatus)
    {
        var clock = new FakeWebhookClock(FixedNow);
        var store = CreateStore(clock);
        var options = CreateOptions();
        var logs = new CapturingLogger<WebhookBackgroundWorker>();
        var replacementClaimed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await WorkerHarness.StartAsync(options, store, clock: clock, dispatch: async (context, _) =>
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            var claimed = await store.Inner.TryClaimAsync(
                context.WebhookId,
                "replacement-worker",
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            replacementClaimed.TrySetResult(claimed);
            return dispatchStatus == WebhookDispatchStatus.Processed
                ? WebhookDispatchResult.Processed()
                : WebhookDispatchResult.Failed(WebhookDispatchFailureKind.Handler, "handler-failed");
        }, logger: logs);
        var record = CreateRecord($"webhook-stale-{dispatchStatus}");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));

        (await replacementClaimed.Task.WaitAsync(TestTimeout)).Should().BeTrue();
        await store.TerminalTransitionRejected.Task.WaitAsync(TestTimeout);
        using var stopCancellation = new CancellationTokenSource(TestTimeout);
        await harness.Worker.StopAsync(stopCancellation.Token);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.ProcessingLeaseOwner.Should().Be("replacement-worker");
        stored.AttemptCount.Should().Be(2);
        logs.Entries.Should().Contain(entry =>
            entry.EventId == 1808 && Equals(entry.Get("FailureCode"), "status-update-failed"));
    }

    [Fact]
    public async Task StartAsync_WhenLeaseExpiresBeforeIgnoredTransition_LogsStatusUpdateFailureWithoutClaimingIgnoredSuccess()
    {
        var clock = new FakeWebhookClock(FixedNow);
        var store = CreateStore(clock);
        var options = CreateOptions();
        var logs = new CapturingLogger<WebhookBackgroundWorker>();
        var replacementClaimed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await WorkerHarness.StartAsync(options, store, clock: clock, dispatch: async (context, _) =>
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            var claimed = await store.Inner.TryClaimAsync(
                context.WebhookId,
                "replacement-worker",
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            replacementClaimed.TrySetResult(claimed);
            return WebhookDispatchResult.Ignored();
        }, logger: logs);
        var record = CreateRecord("webhook-stale-ignored");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));

        (await replacementClaimed.Task.WaitAsync(TestTimeout)).Should().BeTrue();
        await store.Ignored.Task.WaitAsync(TestTimeout);
        using var stopCancellation = new CancellationTokenSource(TestTimeout);
        await harness.Worker.StopAsync(stopCancellation.Token);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().Be(WebhookProcessingStatus.Processing);
        stored.ProcessingLeaseOwner.Should().Be("replacement-worker");
        stored.AttemptCount.Should().Be(2);
        logs.Entries.Should().Contain(entry =>
            entry.EventId == 1808 && Equals(entry.Get("FailureCode"), "status-update-failed"));
        logs.Entries.Should().NotContain(entry => entry.EventId == 1805);
    }

    [Fact]
    public async Task StopAsync_WhenProcessorExceedsShutdownDrainTimeout_LogsOnlySafeTimeoutCode()
    {
        var store = CreateStore();
        var options = CreateOptions(shutdownDrainTimeout: TimeSpan.FromMilliseconds(20));
        var logs = new CapturingLogger<WebhookBackgroundWorker>();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: async (_, token) =>
        {
            started.TrySetResult(true);
            await releaseHandler.Task;
            token.ThrowIfCancellationRequested();
            return WebhookDispatchResult.Processed();
        }, logger: logs);
        var record = CreateRecord("webhook-shutdown-timeout");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await started.Task.WaitAsync(TestTimeout);

        try
        {
            using var stopCancellation = new CancellationTokenSource(TestTimeout);
            var stop = harness.Worker.StopAsync(stopCancellation.Token);

            await stop.WaitAsync(TestTimeout);
            var timeoutLog = logs.Entries.Should().ContainSingle(entry =>
                entry.EventId == 1810 && Equals(entry.Get("FailureCode"), "shutdown-drain-timeout")).Which;
            timeoutLog.Text.Should().Be("Webhook background shutdown drain timed out. FailureCode=shutdown-drain-timeout");
        }
        finally
        {
            releaseHandler.TrySetResult(true);
            await store.Released.Task.WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task StopAsync_WhenActiveHandlerObservesCancellation_WaitsForProcessorCompletionBeforeReturning()
    {
        var store = CreateStore();
        var options = CreateOptions();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: async (_, token) =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult(true));
            started.TrySetResult(true);
            await releaseHandler.Task;
            handlerCompleted.TrySetResult(true);
            token.ThrowIfCancellationRequested();
            return WebhookDispatchResult.Processed();
        });
        var record = CreateRecord("webhook-drain-cancellation");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await started.Task.WaitAsync(TestTimeout);

        try
        {
            using var stopCancellation = new CancellationTokenSource(TestTimeout);
            var stop = harness.Worker.StopAsync(stopCancellation.Token);
            await cancellationObserved.Task.WaitAsync(TestTimeout);

            stop.IsCompleted.Should().BeFalse();
            releaseHandler.TrySetResult(true);
            await handlerCompleted.Task.WaitAsync(TestTimeout);
            await store.Released.Task.WaitAsync(TestTimeout);
            await stop.WaitAsync(TestTimeout);
        }
        finally
        {
            releaseHandler.TrySetResult(true);
        }
    }

    [Fact]
    public async Task StartAsync_WhenHostIsCancelled_DoesNotMarkCancellationAsHandlerFailure()
    {
        var store = CreateStore();
        var options = CreateOptions();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: async (_, token) =>
        {
            started.TrySetResult(true);
            await never.Task.WaitAsync(token);
            return WebhookDispatchResult.Processed();
        });
        var record = CreateRecord("webhook-cancelled");
        await store.Inner.TryCreateAsync(record);
        await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider));
        await started.Task.WaitAsync(TestTimeout);

        using var stopCancellation = new CancellationTokenSource(TestTimeout);
        await harness.Worker.StopAsync(stopCancellation.Token);

        var stored = await store.GetByWebhookIdAsync(record.Id);
        stored!.Status.Should().NotBe(WebhookProcessingStatus.Failed);
        stored.FailureCode.Should().NotBe("handler-failed");
    }

    [Fact]
    public async Task StartAsync_WhenExternalQueueCompletes_DrainsQueuedWorkBeforeStopping()
    {
        var store = CreateStore();
        var options = CreateOptions(queueCapacity: 4);
        var completedDispatch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;
        await using var harness = await WorkerHarness.StartAsync(options, store, dispatch: (_, _) =>
        {
            if (Interlocked.Increment(ref dispatchCount) == 3)
            {
                completedDispatch.TrySetResult(true);
            }

            return Task.FromResult(WebhookDispatchResult.Processed());
        });
        for (var index = 0; index < 3; index++)
        {
            var record = CreateRecord($"webhook-drain-{index}");
            await store.Inner.TryCreateAsync(record);
            (await harness.Queue.TryEnqueueAsync(new WebhookWorkItem(record.Id, record.Provider))).Should().BeTrue();
        }

        await harness.Queue.CompleteAsync();
        await completedDispatch.Task.WaitAsync(TestTimeout);
        await store.Processed.Task.WaitAsync(TestTimeout);

        dispatchCount.Should().Be(3);
        (await store.GetByWebhookIdAsync("webhook-drain-0"))!.Status.Should().Be(WebhookProcessingStatus.Processed);
        (await store.GetByWebhookIdAsync("webhook-drain-1"))!.Status.Should().Be(WebhookProcessingStatus.Processed);
        (await store.GetByWebhookIdAsync("webhook-drain-2"))!.Status.Should().Be(WebhookProcessingStatus.Processed);
    }

    [Fact]
    public async Task StopAsync_WithIdleExternalQueue_CompletesWithoutWaitingForACompletionNotification()
    {
        var store = CreateStore();
        var options = CreateOptions();
        var harness = await WorkerHarness.StartAsync(options, store);
        try
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            var stop = harness.Worker.StopAsync(cancellation.Token);

            await stop.WaitAsync(TestTimeout);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public void BackgroundOptions_DefaultToExplicitlyDisabledSingleWorker()
    {
        var options = new WebhookKitOptions();

        options.Background.Enabled.Should().BeFalse();
        options.Background.WorkerConcurrency.Should().Be(1);
        options.Background.RecoveryInterval.Should().BeGreaterThan(TimeSpan.Zero);
        options.Background.RecoveryBatchSize.Should().BeGreaterThan(0);
        options.Background.LeaseDuration.Should().BeGreaterThan(TimeSpan.Zero);
        options.Background.ShutdownDrainTimeout.Should().BeGreaterThan(TimeSpan.Zero);
        options.Background.RecoveryAge.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsInvalidBackgroundOptions()
    {
        var options = CreateOptions(
            workerConcurrency: 0,
            recoveryInterval: TimeSpan.Zero,
            recoveryBatchSize: 0,
            leaseDuration: TimeSpan.Zero,
            shutdownDrainTimeout: TimeSpan.Zero);
        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Validator_RejectsLeaseShorterThanTheConfiguredRetryWindow()
    {
        var options = new WebhookKitOptions
        {
            Background = new WebhookBackgroundOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(30)
            }
        };
        options.AddProvider("test", provider =>
        {
            provider.Timestamp.AllowMissing = true;
            provider.Retry.MaxAttempts = 4;
            provider.Retry.InitialDelay = TimeSpan.FromSeconds(10);
            provider.Retry.BackoffMultiplier = 2;
        });

        new WebhookKitOptionsValidator().Validate(null, options).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void AddWebhookKit_RegistersHostedWorkerOnlyWhenBackgroundProcessingIsEnabled()
    {
        var disabledServices = new ServiceCollection();
        disabledServices.AddWebhookKit();
        using var disabledProvider = disabledServices.BuildServiceProvider();
        disabledProvider.GetServices<IHostedService>().Should().BeEmpty();

        var enabledServices = new ServiceCollection();
        enabledServices.AddWebhookKit(options => options.Background.Enabled = true);
        using var enabledProvider = enabledServices.BuildServiceProvider();
        enabledProvider.GetServices<IHostedService>().Should().ContainSingle();
        enabledProvider.GetServices<IHostedService>().Single().Should().BeOfType<WebhookBackgroundWorker>();
    }

    private static WebhookKitOptions CreateOptions(
        int workerConcurrency = 1,
        TimeSpan? recoveryInterval = null,
        int recoveryBatchSize = 32,
        TimeSpan? leaseDuration = null,
        int queueCapacity = 8,
        TimeSpan? shutdownDrainTimeout = null)
    {
        return new WebhookKitOptions
        {
            Queue = new WebhookQueueOptions { Capacity = queueCapacity },
            Background = new WebhookBackgroundOptions
            {
                Enabled = true,
                WorkerConcurrency = workerConcurrency,
                RecoveryInterval = recoveryInterval ?? TimeSpan.FromMinutes(1),
                RecoveryBatchSize = recoveryBatchSize,
                LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2),
                RecoveryAge = TimeSpan.FromSeconds(30),
                ShutdownDrainTimeout = shutdownDrainTimeout ?? TimeSpan.FromSeconds(5)
            }
        };
    }

    private static ObservingStore CreateStore(IWebhookClock? clock = null)
    {
        return new ObservingStore(new InMemoryWebhookStore(clock ?? new FakeWebhookClock(FixedNow)));
    }

    private static WebhookRecord CreateRecord(
        string id,
        WebhookProcessingStatus status = WebhookProcessingStatus.Received,
        bool includeRawBody = true)
    {
        return new WebhookRecord
        {
            Id = id,
            CorrelationId = $"correlation-{id}",
            Provider = "test",
            EventId = id,
            EventType = "event.type",
            DeduplicationKey = $"test:{id}",
            HttpMethod = "POST",
            RequestPath = "/webhooks/test",
            Headers = new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Test"] = ["value"]
            },
            ContentType = "application/json",
            ContentLength = 14,
            RawBody = includeRawBody ? Encoding.UTF8.GetBytes("{\"value\":42}") : null,
            ReceivedAt = FixedNow,
            Status = status,
            ProcessedAt = status == WebhookProcessingStatus.Processed ? FixedNow : null
        };
    }

    private static ActivityListener StartActivityListener(string webhookId, ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "WebhookKit" && source.Version == "1.0.0",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (string.Equals(activity.GetTagItem("webhook.id") as string, webhookId, StringComparison.Ordinal))
                {
                    activities.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
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

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private WorkerHarness(
            ServiceProvider provider,
            WebhookBackgroundWorker worker,
            ObservingStore store,
            IWebhookQueue queue,
            DispatchProbe probe)
        {
            Provider = provider;
            Worker = worker;
            Store = store;
            Queue = queue;
            Probe = probe;
        }

        public ServiceProvider Provider { get; }

        public WebhookBackgroundWorker Worker { get; }

        public ObservingStore Store { get; }

        public IWebhookQueue Queue { get; }

        public DispatchProbe Probe { get; }

        public static async Task<WorkerHarness> StartAsync(
            WebhookKitOptions options,
            ObservingStore store,
            IWebhookQueue? queue = null,
            IWebhookClock? clock = null,
            Func<WebhookContext, CancellationToken, Task<WebhookDispatchResult>>? dispatch = null,
            IWebhookRetryExecutor? retryExecutor = null,
            ILogger<WebhookBackgroundWorker>? logger = null)
        {
            var actualClock = clock ?? new FakeWebhookClock(FixedNow);
            var actualQueue = queue ?? new ChannelWebhookQueue(Microsoft.Extensions.Options.Options.Create(options));
            var probe = new DispatchProbe();
            var actualRetryExecutor = retryExecutor ?? new WebhookRetryExecutor(new WebhookRetryPolicy(() => 0), new RecordingRetryDelay());
            var services = new ServiceCollection();
            services.AddSingleton<IWebhookStore>(store);
            services.AddSingleton<IWebhookQueue>(actualQueue);
            services.AddSingleton<IWebhookClock>(actualClock);
            services.AddSingleton(probe);
            services.AddScoped<ScopeMarker>();
            services.AddScoped<IWebhookDeserializer, ProbeDeserializer>();
            services.AddScoped<IWebhookDispatchProcessor>(sp => new TestDispatchProcessor(
                sp.GetRequiredService<ScopeMarker>(),
                probe,
                dispatch ?? ((_, _) => Task.FromResult(WebhookDispatchResult.Processed()))));
            var provider = services.BuildServiceProvider();
            var worker = new WebhookBackgroundWorker(
                actualQueue,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(options),
                actualClock,
                logger,
                timeProvider: null,
                retryExecutor: actualRetryExecutor);
            var harness = new WorkerHarness(provider, worker, store, actualQueue, probe);
            await worker.StartAsync(CancellationToken.None);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            using var cancellation = new CancellationTokenSource(TestTimeout);
            try
            {
                await Worker.StopAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await Provider.DisposeAsync();
        }
    }

    private sealed class ObservingStore(IWebhookStore inner) : IWebhookStore
    {
        public IWebhookStore Inner { get; } = inner;

        public TaskCompletionSource<string> Lookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ClaimAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Ignored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> TerminalTransitionRejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Channel<int> _recoveryCalls = Channel.CreateUnbounded<int>();
        private readonly Channel<string> _processedIds = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        public async Task<string> NextProcessed()
        {
            return await _processedIds.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        }

        public async Task<int> NextRecoveryCall()
        {
            return await _recoveryCalls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        }

        public ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default)
        {
            return Inner.GetAsync(provider, eventId, cancellationToken);
        }

        public ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
        {
            return Inner.TryCreateAsync(record, cancellationToken);
        }

        public async ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
        {
            await Inner.UpdateAsync(record, cancellationToken);
            if (record.Status == WebhookProcessingStatus.Ignored)
            {
                Ignored.TrySetResult(true);
            }
        }

        public async ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default)
        {
            Lookup.TrySetResult(webhookId);
            return await Inner.GetByWebhookIdAsync(webhookId, cancellationToken);
        }

        public async ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var result = await Inner.TryClaimAsync(webhookId, leaseOwner, leaseDuration, cancellationToken);
            ClaimAttempted.TrySetResult(true);
            return result;
        }

        public async ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default)
        {
            var result = await Inner.ReleaseAsync(webhookId, leaseOwner, cancellationToken);
            if (result)
            {
                Released.TrySetResult(true);
            }

            return result;
        }

        public async ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default)
        {
            var result = await Inner.MarkProcessedAsync(webhookId, leaseOwner, processedAt, cancellationToken);
            if (result)
            {
                Processed.TrySetResult(true);
                _processedIds.Writer.TryWrite(webhookId);
            }
            else
            {
                TerminalTransitionRejected.TrySetResult(true);
            }

            return result;
        }

        public async ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default)
        {
            var result = await Inner.MarkFailedAsync(webhookId, leaseOwner, failedAt, failureReason, cancellationToken);
            if (result)
            {
                Failed.TrySetResult(true);
            }
            else
            {
                TerminalTransitionRejected.TrySetResult(true);
            }

            return result;
        }

        public async ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default)
        {
            var result = await Inner.GetRecoverableAsync(now, expiredLeaseAge, limit, cancellationToken);
            _recoveryCalls.Writer.TryWrite(result.Count);
            return result;
        }
    }

    private sealed class DispatchProbe
    {
        public ConcurrentQueue<WebhookContext> Contexts { get; } = new();

        public ConcurrentQueue<Guid> ScopeIds { get; } = new();

        public ConcurrentQueue<Guid> DeserializerScopeIds { get; } = new();

        public TaskCompletionSource<WebhookContext> FirstDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _dispatchCount;
        private int _active;
        private int _maxActive;

        public int DispatchCount => Volatile.Read(ref _dispatchCount);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public void Record(ScopeMarker marker)
        {
            ScopeIds.Enqueue(marker.Id);
        }

        public async Task<WebhookDispatchResult> DispatchAsync(
            ScopeMarker marker,
            WebhookContext context,
            Func<WebhookContext, CancellationToken, Task<WebhookDispatchResult>> behavior,
            CancellationToken cancellationToken)
        {
            Record(marker);
            Contexts.Enqueue(context);
            FirstDispatch.TrySetResult(context);
            Interlocked.Increment(ref _dispatchCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMax(ref _maxActive, active);
            try
            {
                return await behavior(context, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ProbeDeserializer(ScopeMarker marker, DispatchProbe probe) : IWebhookDeserializer
    {
        public T Deserialize<T>(ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.DeserializerScopeIds.Enqueue(marker.Id);
            return JsonSerializer.Deserialize<T>(rawBody.Span, ProbeJsonOptions)
                ?? throw new InvalidOperationException();
        }
    }

    private sealed class TestDispatchProcessor(
        ScopeMarker marker,
        DispatchProbe probe,
        Func<WebhookContext, CancellationToken, Task<WebhookDispatchResult>> behavior) : IWebhookDispatchProcessor
    {
        public Task<WebhookDispatchResult> DispatchAsync(WebhookContext context, CancellationToken cancellationToken = default)
        {
            return probe.DispatchAsync(marker, context, behavior, cancellationToken);
        }
    }

    private sealed class RecordingRetryDelay(bool block = false) : IWebhookRetryDelay
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
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
            var values = state is IEnumerable<KeyValuePair<string, object?>> entries
                ? entries.ToArray()
                : [];
            Entries.Add(new CapturedLog(eventId, values, exception, formatter(state, exception)));
        }
    }

    private sealed record CapturedLog(
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> Values,
        Exception? Exception,
        string Text)
    {
        public object? Get(string name)
        {
            return Values.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value;
        }

        public string GetAllText()
        {
            return Text + "|" + string.Join("|", Values.Select(pair => $"{pair.Key}={pair.Value}"));
        }
    }

    private sealed class AlwaysFullQueue : IWebhookQueue
    {
        private readonly TaskCompletionSource<WebhookWorkItem> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int EnqueueAttempts { get; private set; }

        public ValueTask<bool> TryEnqueueAsync(WebhookWorkItem workItem, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueAttempts++;
            return ValueTask.FromResult(false);
        }

        public async ValueTask<WebhookWorkItem> DequeueAsync(CancellationToken cancellationToken = default)
        {
            return await _never.Task.WaitAsync(cancellationToken);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            _never.TrySetCanceled(cancellationToken);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ActivityHandler : IWebhookHandler<WorkerPayload>
    {
        public Task HandleAsync(WorkerPayload eventData, WebhookContext context, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed record WorkerPayload(int Value);
}
