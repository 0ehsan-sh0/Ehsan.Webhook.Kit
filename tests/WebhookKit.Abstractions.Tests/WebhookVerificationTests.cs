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
    public void VerificationResult_ProviderTimestampIsAdditiveAndDefaultsToNull()
    {
        var providerTimestamp = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var timestampResult = new WebhookVerificationResult(true, null, providerTimestamp);
        var existingResult = new WebhookVerificationResult(true, null);

        timestampResult.ProviderTimestamp.Should().Be(providerTimestamp);
        existingResult.ProviderTimestamp.Should().BeNull();
        WebhookVerificationResult.Success().ProviderTimestamp.Should().BeNull();
    }

    [Fact]
    public void Fail_RequiresReason()
    {
        var result = WebhookVerificationResult.Fail("invalid-signature");

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("invalid-signature");
    }

    [Fact]
    public void VerificationContext_HeadersAreDetachedAndValuesAreReadOnly()
    {
        var source = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X-Signature"] = new List<string> { "signature" }
        };
        var context = new WebhookVerificationContext
        {
            Provider = "stripe",
            RawBody = [1, 2, 3],
            Headers = source
        };

        ((List<string>)source["X-Signature"]).Add("changed");
        var values = (IList<string>)context.Headers["X-Signature"];
        var mutate = () => values.Add("blocked");
        mutate.Should().Throw<NotSupportedException>();
        context.Headers["X-Signature"].Should().Equal("signature");
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
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        store.TryCreateAsync(record).Returns(true);

        (await store.TryCreateAsync(record)).Should().BeTrue();
        (await store.GetAsync("stripe", "evt_123")).Should().BeNull();
    }
}
