// Copyright (c) Ehsan. Licensed under the MIT License.
using FluentAssertions;
using NSubstitute;
using Xunit;
using WebhookKit.Abstractions;

namespace WebhookKit.Abstractions.Tests;

public sealed class WebhookVerificationTests
{
    [Fact]
    public void Success_HasNoReason()
    {
        var result = WebhookVerificationResult.Success();

        result.IsValid.Should().BeTrue();
        result.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Fail_RequiresReason()
    {
        var result = WebhookVerificationResult.Fail("invalid-signature");

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("invalid-signature");
    }

    [Fact]
    public async Task Store_IsMockable_ForAtomicContract()
    {
        var store = Substitute.For<IWebhookStore>();
        var record = new WebhookRecord
        {
            Id = "01K7ABC",
            Provider = "stripe",
            HttpMethod = "POST",
            RequestPath = "/webhooks/stripe",
            Headers = new Dictionary<string, string[]>(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        store.TryCreateAsync(record).Returns(true);

        (await store.TryCreateAsync(record)).Should().BeTrue();
        (await store.GetAsync("stripe", "evt_123")).Should().BeNull();
    }
}
