// Copyright (c) Ehsan. Licensed under the MIT License.
using FluentAssertions;
using Xunit;
using WebhookKit.Abstractions;

namespace WebhookKit.Abstractions.Tests;

public sealed class WebhookProcessingStatusTests
{
    [Fact]
    public void NumericValues_ArePinned()
    {
        ((int)WebhookProcessingStatus.Received).Should().Be(0);
        ((int)WebhookProcessingStatus.Processing).Should().Be(1);
        ((int)WebhookProcessingStatus.Processed).Should().Be(2);
        ((int)WebhookProcessingStatus.Failed).Should().Be(3);
        ((int)WebhookProcessingStatus.Ignored).Should().Be(4);
        ((int)WebhookProcessingStatus.Duplicate).Should().Be(5);
    }
}
