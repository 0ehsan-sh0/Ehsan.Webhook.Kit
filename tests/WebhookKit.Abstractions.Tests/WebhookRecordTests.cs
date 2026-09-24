// Copyright (c) Ehsan. Licensed under the MIT License.
using FluentAssertions;
using Xunit;
using WebhookKit.Abstractions;

namespace WebhookKit.Abstractions.Tests;

public sealed class WebhookRecordTests
{
    [Fact]
    public void CanCreate_WithBytesBody_AndHeaders()
    {
        var record = new WebhookRecord
        {
            Id = "01K7ABC",
            Provider = "stripe",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>> { ["X-Event-Id"] = new[] { "evt_123" } },
            RawBody = [0x7B, 0x7D],
            ReceivedAt = DateTimeOffset.UtcNow,
            Status = WebhookProcessingStatus.Received,
        };

        record.RawBody.Should().Equal(0x7B, 0x7D);
        record.Headers["X-Event-Id"].Should().Equal("evt_123");
        record.Status.Should().Be(WebhookProcessingStatus.Received);
    }

    [Fact]
    public void Headers_AreDetachedAndValuesAreReadOnly()
    {
        var source = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X-Event-Id"] = new List<string> { "evt_source" }
        };
        var record = new WebhookRecord
        {
            Id = "01K7ABC",
            Provider = "stripe",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = source,
            ReceivedAt = DateTimeOffset.UtcNow
        };

        ((List<string>)source["X-Event-Id"]).Add("changed");
        var values = (IList<string>)record.Headers["X-Event-Id"];
        var mutate = () => values.Add("blocked");
        mutate.Should().Throw<NotSupportedException>();
        record.Headers["X-Event-Id"].Should().Equal("evt_source");
        record.CorrelationId.Should().NotBeNullOrWhiteSpace();
        record.CorrelationId.Should().NotBe(record.Id);
        record.CorrelationId.Should().Be(record.CorrelationId);
    }

    [Fact]
    public async Task CorrelationId_IsStableAcrossConcurrentReads()
    {
        var record = new WebhookRecord
        {
            Id = "01K7ABC",
            Provider = "stripe",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            ReceivedAt = DateTimeOffset.UtcNow
        };
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = Enumerable.Range(0, 128)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return record.CorrelationId;
            }))
            .ToArray();

        start.SetResult(true);
        var values = await Task.WhenAll(reads);

        values.Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Status_IsMutable_ForStoreTransitions()
    {
        var record = new WebhookRecord
        {
            Id = "01K7ABC",
            Provider = "stripe",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        record.Status = WebhookProcessingStatus.Processed;
        record.AttemptCount = 1;

        record.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.AttemptCount.Should().Be(1);
    }
}
