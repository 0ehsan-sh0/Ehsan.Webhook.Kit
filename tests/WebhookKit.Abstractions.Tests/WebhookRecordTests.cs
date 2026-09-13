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
            Headers = new Dictionary<string, string[]> { ["X-Event-Id"] = ["evt_123"] },
            RawBody = [0x7B, 0x7D],
            ReceivedAt = DateTimeOffset.UtcNow,
            Status = WebhookProcessingStatus.Received,
        };

        record.RawBody.Should().Equal(0x7B, 0x7D);
        record.Headers["X-Event-Id"].Should().Equal("evt_123");
        record.Status.Should().Be(WebhookProcessingStatus.Received);
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
            Headers = new Dictionary<string, string[]>(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        record.Status = WebhookProcessingStatus.Processed;
        record.AttemptCount = 1;

        record.Status.Should().Be(WebhookProcessingStatus.Processed);
        record.AttemptCount.Should().Be(1);
    }
}
